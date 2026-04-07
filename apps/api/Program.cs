using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using api.Contracts;
using api.Data;
using api.Integrations;
using api.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services.Configure<IntegrationSyncOptions>(builder.Configuration.GetSection("IntegrationSync"));
builder.Services.Configure<NinjaOptions>(builder.Configuration.GetSection("Integrations:Ninja"));
builder.Services.Configure<MicrosoftGraphOptions>(builder.Configuration.GetSection("Integrations:MicrosoftGraph"));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IReferenceMatchService, ReferenceMatchService>();
builder.Services.AddSingleton<INinjaClient, NinjaClient>();
builder.Services.AddSingleton<IMicrosoftGraphClient, MicrosoftGraphClient>();
builder.Services.AddSingleton<ResourceSyncBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ResourceSyncBackgroundService>());

var app = builder.Build();

var serialRegex = new Regex("^[A-Z]{2}[0-9]{6}$", RegexOptions.Compiled);
var activationRegex = new Regex("^[0-9a-f]{4}(-[0-9a-f]{4}){7}$", RegexOptions.Compiled);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data"));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    EnsureTrackedComputerPersonNullable(db);
    EnsureCatEtLicenseComputerNullable(db);
    EnsureSoftDeleteColumns(db);
    EnsureTrackedPersonPhoneColumns(db);
    EnsureCellPhoneAllowanceSchema(db);
    EnsureTrackedComputerSyncControlColumns(db);
    EnsureEntityResourceCoverageSchema(db);
    EnsureIntegrationProviderConfigSchema(db);
    EnsureIntegrationSyncStatusSchema(db);

    if (!db.Users.Any())
    {
        db.Users.Add(new AppUser
        {
            Username = "admin",
            Password = "admin",
            Role = UserRole.Admin,
            CreatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}

app.UseHttpsRedirection();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "WeismanTracker API" }));

app.MapPost("/api/auth/login", (LoginRequest request, AppDbContext db) =>
{
    var user = db.Users.FirstOrDefault(u => u.Username == request.Username && u.Password == request.Password);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var response = new LoginResponse(
        Token: Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
        Username: user.Username,
        Role: user.Role.ToString());

    return Results.Ok(response);
});

app.MapGet("/api/users", (AppDbContext db) =>
{
    var users = db.Users
        .OrderBy(u => u.Username)
        .Select(u => new UserDto(u.Id, u.Username, u.Role.ToString(), u.CreatedAtUtc))
        .ToList();

    return Results.Ok(users);
});

app.MapGet("/api/resource-definitions", async (string? entityType, AppDbContext db) =>
{
    var query = db.ResourceDefinitions.AsQueryable();

    if (!string.IsNullOrWhiteSpace(entityType))
    {
        if (!TryParseResourceEntityType(entityType, out var parsedEntityType))
        {
            return Results.BadRequest(new { message = "entityType must be user|users|computer|computers" });
        }

        query = query.Where(x => x.EntityType == parsedEntityType);
    }

    var definitionRows = await query
        .OrderBy(x => x.EntityType)
        .ThenBy(x => x.Provider)
        .ThenBy(x => x.ResourceType)
        .ToListAsync();

    var definitions = definitionRows
        .Select(x => new ResourceDefinitionDto(
            x.Id,
            ToDtoEntityType(x.EntityType),
            x.Provider,
            x.ResourceType,
            x.DisplayName,
            x.IsEnabled))
        .ToList();

    return Results.Ok(definitions);
});

app.MapGet("/api/entities/{entityType}/{entityId:int}/resource-coverage", async (string entityType, int entityId, AppDbContext db) =>
{
    if (!TryParseResourceEntityType(entityType, out var parsedEntityType))
    {
        return Results.BadRequest(new { message = "entityType must be users|computers" });
    }

    var definitions = await db.ResourceDefinitions
        .Where(x => x.EntityType == parsedEntityType && x.IsEnabled)
        .OrderBy(x => x.Provider)
        .ThenBy(x => x.ResourceType)
        .ToListAsync();

    var references = await db.EntityReferences
        .Where(x => x.EntityType == parsedEntityType && x.EntityId == entityId)
        .ToListAsync();

    var refsByDefinition = references
        .GroupBy(x => x.ResourceDefinitionId)
        .ToDictionary(g => g.Key, g => g.First());

    var rows = definitions.Select(d =>
    {
        refsByDefinition.TryGetValue(d.Id, out var reference);
        var linked = reference is not null && reference.SyncStatus != ReferenceSyncStatus.Missing;

        return new ResourceCoverageRowDto(
            d.Id,
            d.Provider,
            d.ResourceType,
            d.DisplayName,
            Possible: true,
            Linked: linked,
            ExternalId: reference?.ExternalId,
            ExternalKey: reference?.ExternalKey,
            SyncStatus: reference is null ? null : ToDtoReferenceStatus(reference.SyncStatus),
            LastSyncedAtUtc: reference?.LastSyncedAtUtc);
    }).ToList();

    var response = new GetEntityResourceCoverageResponseDto(
        ToDtoEntityType(parsedEntityType),
        entityId,
        rows);

    return Results.Ok(response);
});

app.MapPost("/api/entities/{entityType}/{entityId:int}/references/reconcile", async (string entityType, int entityId, ReconcileEntityReferencesRequestDto request, AppDbContext db) =>
{
    if (!TryParseResourceEntityType(entityType, out var parsedEntityType))
    {
        return Results.BadRequest(new { message = "entityType must be users|computers" });
    }

    var now = DateTime.UtcNow;
    var upserts = request.Upserts ?? [];
    var removes = request.Removes ?? [];

    var warnings = new List<string>();
    var upserted = 0;
    var removed = 0;
    var markedStale = 0;

    var validDefinitionIds = await db.ResourceDefinitions
        .Where(x => x.EntityType == parsedEntityType)
        .Select(x => x.Id)
        .ToListAsync();

    var validDefinitionIdSet = validDefinitionIds.ToHashSet();

    foreach (var upsert in upserts)
    {
        if (!validDefinitionIdSet.Contains(upsert.ResourceDefinitionId))
        {
            warnings.Add($"Unknown ResourceDefinitionId for entity type: {upsert.ResourceDefinitionId}");
            continue;
        }

        if (string.IsNullOrWhiteSpace(upsert.ExternalId))
        {
            warnings.Add($"Skipped upsert with blank ExternalId for definition {upsert.ResourceDefinitionId}");
            continue;
        }

        var externalId = upsert.ExternalId.Trim();
        var externalKey = string.IsNullOrWhiteSpace(upsert.ExternalKey) ? null : upsert.ExternalKey.Trim();

        var existing = await db.EntityReferences.FirstOrDefaultAsync(x =>
            x.EntityType == parsedEntityType &&
            x.EntityId == entityId &&
            x.ResourceDefinitionId == upsert.ResourceDefinitionId &&
            x.ExternalId == externalId);

        if (existing is null)
        {
            existing = new EntityReference
            {
                EntityType = parsedEntityType,
                EntityId = entityId,
                ResourceDefinitionId = upsert.ResourceDefinitionId,
                ExternalId = externalId,
                ExternalKey = externalKey,
                SyncStatus = ReferenceSyncStatus.Linked,
                FirstLinkedAtUtc = now,
                LastSeenAtUtc = now,
                LastSyncedAtUtc = now,
                MetadataJson = upsert.Metadata is null ? null : JsonSerializer.Serialize(upsert.Metadata)
            };

            db.EntityReferences.Add(existing);
        }
        else
        {
            existing.ExternalKey = externalKey;
            existing.SyncStatus = ReferenceSyncStatus.Linked;
            existing.LastSeenAtUtc = now;
            existing.LastSyncedAtUtc = now;

            if (upsert.Metadata is not null)
            {
                existing.MetadataJson = JsonSerializer.Serialize(upsert.Metadata);
            }
        }

        upserted++;
    }

    foreach (var remove in removes)
    {
        var existing = await db.EntityReferences.FirstOrDefaultAsync(x =>
            x.EntityType == parsedEntityType &&
            x.EntityId == entityId &&
            x.ResourceDefinitionId == remove.ResourceDefinitionId &&
            x.ExternalId == remove.ExternalId);

        if (existing is null)
        {
            continue;
        }

        db.EntityReferences.Remove(existing);
        removed++;
    }

    if (request.MarkMissingAsStale && upserts.Count > 0)
    {
        var keepSet = upserts
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId))
            .Select(x => $"{x.ResourceDefinitionId}:{x.ExternalId.Trim()}".ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingRows = await db.EntityReferences
            .Where(x => x.EntityType == parsedEntityType && x.EntityId == entityId)
            .ToListAsync();

        foreach (var row in existingRows)
        {
            var key = $"{row.ResourceDefinitionId}:{row.ExternalId}".ToLowerInvariant();
            if (!keepSet.Contains(key) && row.SyncStatus == ReferenceSyncStatus.Linked)
            {
                row.SyncStatus = ReferenceSyncStatus.Stale;
                row.LastSyncedAtUtc = now;
                markedStale++;
            }
        }
    }

    await db.SaveChangesAsync();

    var response = new ReconcileEntityReferencesResponseDto(
        Upserted: upserted,
        Removed: removed,
        MarkedStale: markedStale,
        Warnings: warnings);

    return Results.Ok(response);
});

app.MapGet("/api/integrations/settings", async (AppDbContext db) =>
{
    var ninjaConfig = await db.IntegrationProviderConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Provider == "Ninja");
    var graphConfig = await db.IntegrationProviderConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Provider == "MicrosoftGraph");

    var ninja = new NinjaIntegrationConfigDto(
        BaseUrl: ninjaConfig?.BaseUrl ?? string.Empty,
        ClientId: ninjaConfig?.ClientId ?? string.Empty,
        HasClientSecret: !string.IsNullOrWhiteSpace(ninjaConfig?.ClientSecret),
        Scope: ninjaConfig?.Scope ?? "monitoring",
        TokenPath: ninjaConfig?.TokenPath ?? "/ws/oauth/token",
        DevicesPath: ninjaConfig?.DevicesPath ?? "/v2/devices",
        PageSize: ninjaConfig?.PageSize is > 0 ? ninjaConfig.PageSize.Value : 200);

    var azureSubs = string.IsNullOrWhiteSpace(graphConfig?.AzureSubscriptionIdsCsv)
        ? []
        : graphConfig.AzureSubscriptionIdsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    var graph = new MicrosoftGraphIntegrationConfigDto(
        TenantId: graphConfig?.TenantId ?? string.Empty,
        ClientId: graphConfig?.ClientId ?? string.Empty,
        HasClientSecret: !string.IsNullOrWhiteSpace(graphConfig?.ClientSecret),
        GraphBaseUrl: graphConfig?.BaseUrl ?? "https://graph.microsoft.com",
        ResourceManagerBaseUrl: graphConfig?.ResourceManagerBaseUrl ?? "https://management.azure.com",
        PageSize: graphConfig?.PageSize is > 0 ? graphConfig.PageSize.Value : 999,
        AzureSubscriptionIds: azureSubs);

    return Results.Ok(new IntegrationSettingsDto(ninja, graph));
});

app.MapPut("/api/integrations/settings/ninja", async (UpdateNinjaIntegrationConfigRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.ClientId))
    {
        return Results.BadRequest(new { message = "BaseUrl and ClientId are required." });
    }

    if (request.PageSize <= 0)
    {
        return Results.BadRequest(new { message = "PageSize must be greater than 0." });
    }

    var config = await db.IntegrationProviderConfigs.FirstOrDefaultAsync(x => x.Provider == "Ninja");
    if (config is null)
    {
        config = new IntegrationProviderConfig { Provider = "Ninja" };
        db.IntegrationProviderConfigs.Add(config);
    }

    config.BaseUrl = request.BaseUrl.Trim();
    config.ClientId = request.ClientId.Trim();
    if (!string.IsNullOrWhiteSpace(request.ClientSecret))
    {
        config.ClientSecret = request.ClientSecret.Trim();
    }

    config.Scope = string.IsNullOrWhiteSpace(request.Scope) ? "monitoring" : request.Scope.Trim();
    config.TokenPath = string.IsNullOrWhiteSpace(request.TokenPath) ? "/ws/oauth/token" : request.TokenPath.Trim();
    config.DevicesPath = string.IsNullOrWhiteSpace(request.DevicesPath) ? "/v2/devices" : request.DevicesPath.Trim();
    config.PageSize = request.PageSize;
    config.UpdatedAtUtc = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Ninja settings saved." });
});

app.MapPut("/api/integrations/settings/microsoft-graph", async (UpdateMicrosoftGraphIntegrationConfigRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.ClientId))
    {
        return Results.BadRequest(new { message = "TenantId and ClientId are required." });
    }

    if (request.PageSize <= 0)
    {
        return Results.BadRequest(new { message = "PageSize must be greater than 0." });
    }

    var config = await db.IntegrationProviderConfigs.FirstOrDefaultAsync(x => x.Provider == "MicrosoftGraph");
    if (config is null)
    {
        config = new IntegrationProviderConfig { Provider = "MicrosoftGraph" };
        db.IntegrationProviderConfigs.Add(config);
    }

    config.TenantId = request.TenantId.Trim();
    config.ClientId = request.ClientId.Trim();
    if (!string.IsNullOrWhiteSpace(request.ClientSecret))
    {
        config.ClientSecret = request.ClientSecret.Trim();
    }

    config.BaseUrl = string.IsNullOrWhiteSpace(request.GraphBaseUrl) ? "https://graph.microsoft.com" : request.GraphBaseUrl.Trim();
    config.ResourceManagerBaseUrl = string.IsNullOrWhiteSpace(request.ResourceManagerBaseUrl)
        ? "https://management.azure.com"
        : request.ResourceManagerBaseUrl.Trim();
    config.PageSize = request.PageSize;

    var subscriptions = request.AzureSubscriptionIds?
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList() ?? [];
    config.AzureSubscriptionIdsCsv = subscriptions.Count == 0 ? null : string.Join(',', subscriptions);

    config.UpdatedAtUtc = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Microsoft Graph settings saved." });
});

app.MapGet("/api/integrations/sync-status", async (ResourceSyncBackgroundService syncService, CancellationToken cancellationToken) =>
{
    var status = await syncService.GetSyncStatusSnapshotAsync(cancellationToken);
    return Results.Ok(status);
});

app.MapPost("/api/integrations/sync-now/ninja", async (ResourceSyncBackgroundService syncService, CancellationToken cancellationToken) =>
{
    var result = await syncService.RunNinjaSyncNowAsync(cancellationToken);
    return result.Success
        ? Results.Ok(result)
        : Results.Conflict(result);
});

app.MapPost("/api/integrations/sync-now/microsoft", async (ResourceSyncBackgroundService syncService, CancellationToken cancellationToken) =>
{
    var result = await syncService.RunMicrosoftSyncNowAsync(cancellationToken);
    return result.Success
        ? Results.Ok(result)
        : Results.Conflict(result);
});

app.MapGet("/api/allowances/cell-phone", async (AppDbContext db, int page = 1, int pageSize = 50, string? search = null, string? sortBy = null, string? sortDir = null, string? filter = null) =>
{
    var safePage = Math.Max(1, page);
    var safePageSize = Math.Clamp(pageSize, 1, 500);
    var searchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
    var normalizedFilter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
    var normalizedSortBy = string.IsNullOrWhiteSpace(sortBy) ? "person" : sortBy.Trim().ToLowerInvariant();
    var desc = string.Equals(sortDir?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

    var query = db.CellPhoneAllowances
        .AsNoTracking()
        .Include(x => x.TrackedPerson)
        .Where(x => x.DeletedAtUtc == null && x.TrackedPerson != null && x.TrackedPerson.DeletedAtUtc == null)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(searchTerm))
    {
        query = query.Where(x =>
            x.MobilePhoneNumber.ToLower().Contains(searchTerm)
            || x.TrackedPerson!.FullName.ToLower().Contains(searchTerm)
            || (x.TrackedPerson.Email != null && x.TrackedPerson.Email.ToLower().Contains(searchTerm)));
    }

    query = normalizedFilter switch
    {
        "granted" => query.Where(x => x.AllowanceGranted),
        "notgranted" => query.Where(x => !x.AllowanceGranted),
        _ => query
    };

    query = (normalizedSortBy, desc) switch
    {
        ("mobilenumber", false) => query.OrderBy(x => x.MobilePhoneNumber).ThenBy(x => x.Id),
        ("mobilenumber", true) => query.OrderByDescending(x => x.MobilePhoneNumber).ThenByDescending(x => x.Id),
        ("approvedat", false) => query.OrderBy(x => x.ApprovedAtUtc == null).ThenBy(x => x.ApprovedAtUtc).ThenBy(x => x.Id),
        ("approvedat", true) => query.OrderByDescending(x => x.ApprovedAtUtc == null).ThenByDescending(x => x.ApprovedAtUtc).ThenByDescending(x => x.Id),
        ("granted", false) => query.OrderBy(x => x.AllowanceGranted).ThenBy(x => x.Id),
        ("granted", true) => query.OrderByDescending(x => x.AllowanceGranted).ThenByDescending(x => x.Id),
        ("createdat", false) => query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
        ("createdat", true) => query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id),
        ("person", true) => query.OrderByDescending(x => x.TrackedPerson!.FullName).ThenByDescending(x => x.Id),
        _ => query.OrderBy(x => x.TrackedPerson!.FullName).ThenBy(x => x.Id)
    };

    var totalCount = await query.CountAsync();
    var items = await query
        .Skip((safePage - 1) * safePageSize)
        .Take(safePageSize)
        .Select(x => new CellPhoneAllowanceDto(
            x.Id,
            x.TrackedPersonId,
            x.TrackedPerson!.FullName,
            x.TrackedPerson.Email,
            x.MobilePhoneNumber,
            x.AllowanceGranted,
            x.ApprovedAtUtc,
            x.CreatedAtUtc))
        .ToListAsync();

    return Results.Ok(new PagedResultDto<CellPhoneAllowanceDto>(items, totalCount, safePage, safePageSize));
});

app.MapPost("/api/allowances/cell-phone", async (CreateCellPhoneAllowanceRequest request, AppDbContext db) =>
{
    var person = await db.TrackedPeople.FirstOrDefaultAsync(x => x.Id == request.TrackedPersonId && x.DeletedAtUtc == null);
    if (person is null)
    {
        return Results.BadRequest(new { message = "TrackedPersonId does not exist." });
    }

    if (string.IsNullOrWhiteSpace(request.MobilePhoneNumber))
    {
        return Results.BadRequest(new { message = "Mobile phone number is required." });
    }

    var allowance = new CellPhoneAllowance
    {
        TrackedPersonId = request.TrackedPersonId,
        MobilePhoneNumber = request.MobilePhoneNumber.Trim(),
        AllowanceGranted = request.AllowanceGranted,
        ApprovedAtUtc = request.ApprovedAtUtc?.Date,
        CreatedAtUtc = DateTime.UtcNow
    };

    db.CellPhoneAllowances.Add(allowance);
    await db.SaveChangesAsync();

    return Results.Ok(new CellPhoneAllowanceDto(
        allowance.Id,
        allowance.TrackedPersonId,
        person.FullName,
        person.Email,
        allowance.MobilePhoneNumber,
        allowance.AllowanceGranted,
        allowance.ApprovedAtUtc,
        allowance.CreatedAtUtc));
});

app.MapPut("/api/allowances/cell-phone/{id:int}", async (int id, CreateCellPhoneAllowanceRequest request, AppDbContext db) =>
{
    var allowance = await db.CellPhoneAllowances.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null);
    if (allowance is null)
    {
        return Results.NotFound(new { message = "Cell phone allowance not found." });
    }

    var person = await db.TrackedPeople.FirstOrDefaultAsync(x => x.Id == request.TrackedPersonId && x.DeletedAtUtc == null);
    if (person is null)
    {
        return Results.BadRequest(new { message = "TrackedPersonId does not exist." });
    }

    if (string.IsNullOrWhiteSpace(request.MobilePhoneNumber))
    {
        return Results.BadRequest(new { message = "Mobile phone number is required." });
    }

    allowance.TrackedPersonId = request.TrackedPersonId;
    allowance.MobilePhoneNumber = request.MobilePhoneNumber.Trim();
    allowance.AllowanceGranted = request.AllowanceGranted;
    allowance.ApprovedAtUtc = request.ApprovedAtUtc?.Date;

    await db.SaveChangesAsync();

    return Results.Ok(new CellPhoneAllowanceDto(
        allowance.Id,
        allowance.TrackedPersonId,
        person.FullName,
        person.Email,
        allowance.MobilePhoneNumber,
        allowance.AllowanceGranted,
        allowance.ApprovedAtUtc,
        allowance.CreatedAtUtc));
});

app.MapDelete("/api/allowances/cell-phone/{id:int}", async (int id, AppDbContext db) =>
{
    var allowance = await db.CellPhoneAllowances.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null);
    if (allowance is null)
    {
        return Results.NotFound(new { message = "Cell phone allowance not found." });
    }

    allowance.DeletedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/catet/people", async (AppDbContext db, int page = 1, int pageSize = 50, string? search = null, string? sortBy = null, string? sortDir = null, string? filter = null) =>
{
    var safePage = Math.Max(1, page);
    var safePageSize = Math.Clamp(pageSize, 1, 500);
    var searchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
    var normalizedFilter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
    var normalizedSortBy = string.IsNullOrWhiteSpace(sortBy) ? "fullname" : sortBy.Trim().ToLowerInvariant();
    var desc = string.Equals(sortDir?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

    var query = db.TrackedPeople
        .AsNoTracking()
        .Where(p => p.DeletedAtUtc == null);

    if (!string.IsNullOrWhiteSpace(searchTerm))
    {
        query = query.Where(p =>
            p.FullName.ToLower().Contains(searchTerm)
            || (p.Email != null && p.Email.ToLower().Contains(searchTerm))
            || (p.MobilePhone != null && p.MobilePhone.ToLower().Contains(searchTerm))
            || (p.BusinessPhone != null && p.BusinessPhone.ToLower().Contains(searchTerm)));
    }

    query = normalizedFilter switch
    {
        "withemail" => query.Where(p => p.Email != null && p.Email != ""),
        "withoutemail" => query.Where(p => p.Email == null || p.Email == ""),
        _ => query
    };

    query = (normalizedSortBy, desc) switch
    {
        ("email", false) => query.OrderBy(p => p.Email == null).ThenBy(p => p.Email).ThenBy(p => p.Id),
        ("email", true) => query.OrderByDescending(p => p.Email == null).ThenByDescending(p => p.Email).ThenByDescending(p => p.Id),
        ("mobilephone", false) => query.OrderBy(p => p.MobilePhone == null).ThenBy(p => p.MobilePhone).ThenBy(p => p.Id),
        ("mobilephone", true) => query.OrderByDescending(p => p.MobilePhone == null).ThenByDescending(p => p.MobilePhone).ThenByDescending(p => p.Id),
        ("businessphone", false) => query.OrderBy(p => p.BusinessPhone == null).ThenBy(p => p.BusinessPhone).ThenBy(p => p.Id),
        ("businessphone", true) => query.OrderByDescending(p => p.BusinessPhone == null).ThenByDescending(p => p.BusinessPhone).ThenByDescending(p => p.Id),
        ("createdat", false) => query.OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id),
        ("createdat", true) => query.OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.Id),
        ("fullname", true) => query.OrderByDescending(p => p.FullName).ThenByDescending(p => p.Id),
        _ => query.OrderBy(p => p.FullName).ThenBy(p => p.Id)
    };

    var totalCount = await query.CountAsync();
    var people = await query
        .Skip((safePage - 1) * safePageSize)
        .Take(safePageSize)
        .Select(p => new TrackedPersonDto(p.Id, p.FullName, p.Email, p.MobilePhone, p.BusinessPhone, p.CreatedAtUtc))
        .ToListAsync();

    return Results.Ok(new PagedResultDto<TrackedPersonDto>(people, totalCount, safePage, safePageSize));
});

app.MapPost("/api/catet/people", async (CreateTrackedPersonRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.FullName))
    {
        return Results.BadRequest(new { message = "Full name is required." });
    }

    var person = new TrackedPerson
    {
        FullName = request.FullName.Trim(),
        Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
        MobilePhone = string.IsNullOrWhiteSpace(request.MobilePhone) ? null : request.MobilePhone.Trim(),
        BusinessPhone = string.IsNullOrWhiteSpace(request.BusinessPhone) ? null : request.BusinessPhone.Trim(),
        CreatedAtUtc = DateTime.UtcNow
    };

    db.TrackedPeople.Add(person);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "Email already exists." });
    }

    return Results.Ok(new TrackedPersonDto(person.Id, person.FullName, person.Email, person.MobilePhone, person.BusinessPhone, person.CreatedAtUtc));
});

app.MapPut("/api/catet/people/{id:int}", async (int id, CreateTrackedPersonRequest request, AppDbContext db) =>
{
    var person = await db.TrackedPeople.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAtUtc == null);
    if (person is null)
    {
        return Results.NotFound(new { message = "Person not found." });
    }

    if (string.IsNullOrWhiteSpace(request.FullName))
    {
        return Results.BadRequest(new { message = "Full name is required." });
    }

    person.FullName = request.FullName.Trim();
    person.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
    person.MobilePhone = string.IsNullOrWhiteSpace(request.MobilePhone) ? null : request.MobilePhone.Trim();
    person.BusinessPhone = string.IsNullOrWhiteSpace(request.BusinessPhone) ? null : request.BusinessPhone.Trim();

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "Email already exists." });
    }

    return Results.Ok(new TrackedPersonDto(person.Id, person.FullName, person.Email, person.MobilePhone, person.BusinessPhone, person.CreatedAtUtc));
});

app.MapDelete("/api/catet/people/{id:int}", async (int id, AppDbContext db) =>
{
    var person = await db.TrackedPeople.FirstOrDefaultAsync(p => p.Id == id && p.DeletedAtUtc == null);
    if (person is null)
    {
        return Results.NotFound(new { message = "Person not found." });
    }

    var hasComputers = await db.TrackedComputers.AnyAsync(c => c.TrackedPersonId == id && c.DeletedAtUtc == null);
    if (hasComputers)
    {
        return Results.BadRequest(new { message = "Cannot delete person with assigned computers. Unassign or reassign first." });
    }

    var hasAllowances = await db.CellPhoneAllowances.AnyAsync(x => x.TrackedPersonId == id && x.DeletedAtUtc == null);
    if (hasAllowances)
    {
        return Results.BadRequest(new { message = "Cannot delete person with cell phone allowance records. Remove those records first." });
    }

    person.DeletedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/catet/computers", async (AppDbContext db, int page = 1, int pageSize = 50, string? search = null, string? sortBy = null, string? sortDir = null, string? filter = null, string? visibility = null, string? category = null) =>
{
    var safePage = Math.Max(1, page);
    var safePageSize = Math.Clamp(pageSize, 1, 500);
    var searchTerm = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
    var normalizedFilter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();
    var normalizedVisibility = string.IsNullOrWhiteSpace(visibility) ? "visible" : visibility.Trim().ToLowerInvariant();
    var normalizedCategoryFilter = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
    var normalizedCategory = NormalizeAssetCategory(category);
    var normalizedSortBy = string.IsNullOrWhiteSpace(sortBy) ? "hostname" : sortBy.Trim().ToLowerInvariant();
    var desc = string.Equals(sortDir?.Trim(), "desc", StringComparison.OrdinalIgnoreCase);

    var query = db.TrackedComputers
        .AsNoTracking()
        .Where(c => c.DeletedAtUtc == null)
        .Include(c => c.TrackedPerson)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(searchTerm))
    {
        query = query.Where(c =>
            c.Hostname.ToLower().Contains(searchTerm)
            || c.AssetTag.ToLower().Contains(searchTerm)
            || (c.TrackedPerson != null && c.TrackedPerson.FullName.ToLower().Contains(searchTerm)));
    }

    query = normalizedFilter switch
    {
        "assigned" => query.Where(c => c.TrackedPersonId != null),
        "unassigned" => query.Where(c => c.TrackedPersonId == null),
        _ => query
    };

    query = normalizedVisibility switch
    {
        "hidden" => query.Where(c => c.HiddenFromTable),
        "mobile" => query.Where(c => c.IsMobileDevice),
        "all" => query,
        _ => query.Where(c => !c.HiddenFromTable)
    };

    if (string.Equals(normalizedCategoryFilter, "mobile", StringComparison.OrdinalIgnoreCase))
    {
        query = query.Where(c => c.AssetCategory == "Phone" || c.AssetCategory == "Tablet");
    }
    else if (string.Equals(normalizedCategoryFilter, "other", StringComparison.OrdinalIgnoreCase))
    {
        query = query.Where(c => c.AssetCategory != "Computer" && c.AssetCategory != "Phone" && c.AssetCategory != "Tablet");
    }
    else if (!string.IsNullOrWhiteSpace(normalizedCategory) && !string.Equals(normalizedCategory, "all", StringComparison.OrdinalIgnoreCase))
    {
        query = query.Where(c => c.AssetCategory == normalizedCategory);
    }

    query = (normalizedSortBy, desc) switch
    {
        ("assignee", false) => query.OrderBy(c => c.TrackedPerson == null).ThenBy(c => c.TrackedPerson != null ? c.TrackedPerson.FullName : string.Empty).ThenBy(c => c.Id),
        ("assignee", true) => query.OrderByDescending(c => c.TrackedPerson == null).ThenByDescending(c => c.TrackedPerson != null ? c.TrackedPerson.FullName : string.Empty).ThenByDescending(c => c.Id),
        ("assettag", false) => query.OrderBy(c => c.AssetTag).ThenBy(c => c.Id),
        ("assettag", true) => query.OrderByDescending(c => c.AssetTag).ThenByDescending(c => c.Id),
        ("createdat", false) => query.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id),
        ("createdat", true) => query.OrderByDescending(c => c.CreatedAtUtc).ThenByDescending(c => c.Id),
        ("hostname", true) => query.OrderByDescending(c => c.Hostname).ThenByDescending(c => c.Id),
        _ => query.OrderBy(c => c.Hostname).ThenBy(c => c.Id)
    };

    var totalCount = await query.CountAsync();
    var computers = await query
        .Skip((safePage - 1) * safePageSize)
        .Take(safePageSize)
        .Select(c => new TrackedComputerDto(
            c.Id,
            c.Hostname,
            c.AssetTag,
            c.TrackedPersonId,
            c.TrackedPerson != null ? c.TrackedPerson.FullName : null,
            c.CreatedAtUtc,
            c.ExcludeFromSync,
            c.HiddenFromTable,
            c.IsMobileDevice,
            c.AssetCategory))
        .ToListAsync();

    return Results.Ok(new PagedResultDto<TrackedComputerDto>(computers, totalCount, safePage, safePageSize));
});

app.MapPost("/api/catet/computers", async (CreateTrackedComputerRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Hostname) || string.IsNullOrWhiteSpace(request.AssetTag))
    {
        return Results.BadRequest(new { message = "Hostname and AssetTag are required." });
    }

    TrackedPerson? person = null;
    if (request.TrackedPersonId is int trackedPersonId)
    {
        person = await db.TrackedPeople.FirstOrDefaultAsync(p => p.Id == trackedPersonId && p.DeletedAtUtc == null);
        if (person is null)
        {
            return Results.BadRequest(new { message = "TrackedPersonId does not exist." });
        }
    }

    var computer = new TrackedComputer
    {
        Hostname = request.Hostname.Trim(),
        AssetTag = request.AssetTag.Trim(),
        TrackedPersonId = request.TrackedPersonId,
        CreatedAtUtc = DateTime.UtcNow,
        AssetCategory = "Computer"
    };

    db.TrackedComputers.Add(computer);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "Asset tag already exists." });
    }

    return Results.Ok(new TrackedComputerDto(
        computer.Id,
        computer.Hostname,
        computer.AssetTag,
        computer.TrackedPersonId,
        person?.FullName,
        computer.CreatedAtUtc,
        computer.ExcludeFromSync,
        computer.HiddenFromTable,
        computer.IsMobileDevice,
        computer.AssetCategory));
});

app.MapPut("/api/catet/computers/{id:int}", async (int id, CreateTrackedComputerRequest request, AppDbContext db) =>
{
    var computer = await db.TrackedComputers.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAtUtc == null);
    if (computer is null)
    {
        return Results.NotFound(new { message = "Computer not found." });
    }

    if (string.IsNullOrWhiteSpace(request.Hostname) || string.IsNullOrWhiteSpace(request.AssetTag))
    {
        return Results.BadRequest(new { message = "Hostname and AssetTag are required." });
    }

    TrackedPerson? person = null;
    if (request.TrackedPersonId is int trackedPersonId)
    {
        person = await db.TrackedPeople.FirstOrDefaultAsync(p => p.Id == trackedPersonId && p.DeletedAtUtc == null);
        if (person is null)
        {
            return Results.BadRequest(new { message = "TrackedPersonId does not exist." });
        }
    }

    computer.Hostname = request.Hostname.Trim();
    computer.AssetTag = request.AssetTag.Trim();
    computer.TrackedPersonId = request.TrackedPersonId;

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "Asset tag already exists." });
    }

    return Results.Ok(new TrackedComputerDto(
        computer.Id,
        computer.Hostname,
        computer.AssetTag,
        computer.TrackedPersonId,
        person?.FullName,
        computer.CreatedAtUtc,
        computer.ExcludeFromSync,
        computer.HiddenFromTable,
        computer.IsMobileDevice,
        computer.AssetCategory));
});

app.MapPut("/api/catet/computers/{id:int}/flags", async (int id, UpdateTrackedComputerFlagsRequest request, AppDbContext db) =>
{
    var computer = await db.TrackedComputers.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAtUtc == null);
    if (computer is null)
    {
        return Results.NotFound(new { message = "Computer not found." });
    }

    if (request.ExcludeFromSync is bool excludeFromSync)
    {
        computer.ExcludeFromSync = excludeFromSync;
    }

    if (request.HiddenFromTable is bool hiddenFromTable)
    {
        computer.HiddenFromTable = hiddenFromTable;
    }

    if (request.AssetCategory is not null)
    {
        computer.AssetCategory = NormalizeAssetCategory(request.AssetCategory) ?? "Computer";
        computer.IsMobileDevice = !computer.AssetCategory.Equals("Computer", StringComparison.OrdinalIgnoreCase);
    }

    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapDelete("/api/catet/computers/{id:int}", async (int id, AppDbContext db) =>
{
    var computer = await db.TrackedComputers.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAtUtc == null);
    if (computer is null)
    {
        return Results.NotFound(new { message = "Computer not found." });
    }

    var linkedLicenses = await db.CatEtLicenses.Where(l => l.TrackedComputerId == id).ToListAsync();
    foreach (var license in linkedLicenses)
    {
        license.TrackedComputerId = null;
    }

    computer.DeletedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/catet/licenses", async (AppDbContext db) =>
{
    var licenses = await db.CatEtLicenses
        .Where(l => l.DeletedAtUtc == null)
        .Include(l => l.TrackedComputer)
        .ThenInclude(c => c!.TrackedPerson)
        .OrderByDescending(l => l.CreatedAtUtc)
        .Select(l => new CatEtLicenseDto(
            l.Id,
            l.SerialNumber,
            l.ActivationId,
            l.Status.ToString(),
            l.ActivatedAtUtc,
            l.LastResetAtUtc,
            l.TrackedComputerId,
            l.TrackedComputer != null ? l.TrackedComputer.Hostname : null,
            l.TrackedComputer != null ? l.TrackedComputer.AssetTag : null,
            l.TrackedComputer != null ? l.TrackedComputer.TrackedPersonId : null,
            l.TrackedComputer != null && l.TrackedComputer.TrackedPerson != null ? l.TrackedComputer.TrackedPerson.FullName : null,
            l.CreatedAtUtc))
        .ToListAsync();

    return Results.Ok(licenses);
});

app.MapGet("/api/catet/licenses/{id:int}/events", async (int id, AppDbContext db) =>
{
    var licenseExists = await db.CatEtLicenses.AnyAsync(l => l.Id == id && l.DeletedAtUtc == null);
    if (!licenseExists)
    {
        return Results.NotFound(new { message = "License not found." });
    }

    var events = await db.CatEtActivationEvents
        .Where(e => e.CatEtLicenseId == id)
        .OrderByDescending(e => e.OccurredAtUtc)
        .Select(e => new CatEtActivationEventDto(e.Id, e.CatEtLicenseId, e.EventType, e.Notes, e.OccurredAtUtc))
        .ToListAsync();

    return Results.Ok(events);
});

app.MapGet("/api/catet/activity", async (AppDbContext db) =>
{
    var events = await db.CatEtActivationEvents
        .Include(e => e.CatEtLicense)
            .ThenInclude(l => l!.TrackedComputer)
                .ThenInclude(c => c!.TrackedPerson)
        .OrderByDescending(e => e.OccurredAtUtc)
        .Select(e => new CatEtActivationActivityRowDto(
            e.Id,
            e.CatEtLicenseId,
            e.CatEtLicense != null ? e.CatEtLicense.SerialNumber : "Unknown",
            e.EventType,
            e.Notes,
            e.OccurredAtUtc,
            e.CatEtLicense != null && e.CatEtLicense.TrackedComputer != null ? e.CatEtLicense.TrackedComputer.Hostname : null,
            e.CatEtLicense != null && e.CatEtLicense.TrackedComputer != null ? e.CatEtLicense.TrackedComputer.AssetTag : null,
            e.CatEtLicense != null && e.CatEtLicense.TrackedComputer != null && e.CatEtLicense.TrackedComputer.TrackedPerson != null
                ? e.CatEtLicense.TrackedComputer.TrackedPerson.FullName
                : null))
        .ToListAsync();

    return Results.Ok(events);
});

app.MapPost("/api/catet/licenses", async (CreateCatEtLicenseRequest request, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.SerialNumber) || string.IsNullOrWhiteSpace(request.ActivationId))
    {
        return Results.BadRequest(new { message = "SerialNumber and ActivationId are required." });
    }

    var serial = NormalizeSerialNumber(request.SerialNumber);
    var activationId = NormalizeActivationId(request.ActivationId);

    if (!serialRegex.IsMatch(serial))
    {
        return Results.BadRequest(new { message = "SerialNumber must match format AA123456 (example: EA021234)." });
    }

    if (!activationRegex.IsMatch(activationId))
    {
        return Results.BadRequest(new { message = "ActivationId must match xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xxxx (hex)." });
    }

    if (await db.CatEtLicenses.AnyAsync(l => l.SerialNumber == serial))
    {
        return Results.Conflict(new { message = "Serial number already exists." });
    }

    TrackedComputer? computer = null;
    if (request.TrackedComputerId is int trackedComputerId)
    {
        computer = await db.TrackedComputers.Include(c => c.TrackedPerson).FirstOrDefaultAsync(c => c.Id == trackedComputerId);
        if (computer is null)
        {
            return Results.BadRequest(new { message = "TrackedComputerId does not exist." });
        }
    }

    var license = new CatEtLicense
    {
        SerialNumber = serial,
        LicenseKey = activationId,
        ActivationId = activationId,
        Status = CatEtLicenseStatus.Available,
        TrackedComputerId = request.TrackedComputerId,
        CreatedAtUtc = DateTime.UtcNow
    };

    db.CatEtLicenses.Add(license);
    db.CatEtActivationEvents.Add(new CatEtActivationEvent
    {
        CatEtLicense = license,
        EventType = "Issued",
        Notes = "Initial activation ID issued"
    });

    await db.SaveChangesAsync();

    return Results.Ok(new CatEtLicenseDto(
        license.Id,
        license.SerialNumber,
        license.ActivationId,
        license.Status.ToString(),
        license.ActivatedAtUtc,
        license.LastResetAtUtc,
        license.TrackedComputerId,
        computer?.Hostname,
        computer?.AssetTag,
        computer?.TrackedPersonId,
        computer?.TrackedPerson?.FullName,
        license.CreatedAtUtc));
});

app.MapPost("/api/catet/licenses/import", async (HttpRequest request, AppDbContext db) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Upload must be multipart/form-data." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files["file"] ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { message = "No file uploaded." });
    }

    var forceOverwrite = bool.TryParse(form["forceOverwrite"].FirstOrDefault(), out var parsed) && parsed;

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not ".xlsx" and not ".csv")
    {
        return Results.BadRequest(new { message = "Only .xlsx and .csv files are supported." });
    }

    List<ImportRow> rows;
    await using (var stream = file.OpenReadStream())
    {
        rows = ParseImportRows(stream, extension);
    }

    if (rows.Count == 0)
    {
        return Results.BadRequest(new { message = "No valid data rows found. Expected Serial Number in column A and Activation ID in column B." });
    }

    var existingBySerial = (await db.CatEtLicenses.ToListAsync())
        .ToDictionary(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase);
    var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var importedCount = 0;
    var unchangedDuplicateCount = 0;
    var mismatchCount = 0;
    var overwrittenCount = 0;

    var invalidRows = new List<string>();
    var duplicateSerialNumbers = new List<string>();
    var mismatchSerialNumbers = new List<string>();
    var overwrittenSerialNumbers = new List<string>();

    foreach (var row in rows)
    {
        var serial = NormalizeSerialNumber(row.SerialNumber);
        var activationId = NormalizeActivationId(row.ActivationId);

        if (!serialRegex.IsMatch(serial))
        {
            invalidRows.Add($"Row {row.RowNumber}: SerialNumber must match AA123456.");
            continue;
        }

        if (!activationRegex.IsMatch(activationId))
        {
            invalidRows.Add($"Row {row.RowNumber}: ActivationId must be 8 groups of 4 hex chars.");
            continue;
        }

        if (!seenSerials.Add(serial))
        {
            invalidRows.Add($"Row {row.RowNumber}: Serial number '{serial}' appears more than once in the import file.");
            continue;
        }

        if (existingBySerial.TryGetValue(serial, out var existing))
        {
            if (existing.DeletedAtUtc is not null)
            {
                unchangedDuplicateCount++;
                duplicateSerialNumbers.Add(serial);
                invalidRows.Add($"Row {row.RowNumber}: Serial number '{serial}' exists as a soft-deleted record and cannot be re-imported.");
                continue;
            }

            if (string.Equals(existing.ActivationId, activationId, StringComparison.OrdinalIgnoreCase))
            {
                unchangedDuplicateCount++;
                duplicateSerialNumbers.Add(serial);
                continue;
            }

            mismatchCount++;
            mismatchSerialNumbers.Add(serial);

            if (forceOverwrite)
            {
                existing.ActivationId = activationId;
                existing.LicenseKey = activationId;
                existing.Status = CatEtLicenseStatus.Available;
                existing.ActivatedAtUtc = null;
                existing.LastResetAtUtc = DateTime.UtcNow;

                db.CatEtActivationEvents.Add(new CatEtActivationEvent
                {
                    CatEtLicenseId = existing.Id,
                    EventType = "Reset",
                    Notes = "Activation ID overwritten by spreadsheet import"
                });

                overwrittenCount++;
                overwrittenSerialNumbers.Add(serial);
            }

            continue;
        }

        var license = new CatEtLicense
        {
            SerialNumber = serial,
            LicenseKey = activationId,
            ActivationId = activationId,
            Status = CatEtLicenseStatus.Available,
            CreatedAtUtc = DateTime.UtcNow
        };

        db.CatEtLicenses.Add(license);
        db.CatEtActivationEvents.Add(new CatEtActivationEvent
        {
            CatEtLicense = license,
            EventType = "Issued",
            Notes = "Imported from spreadsheet"
        });

        importedCount++;
    }

    if (importedCount > 0 || overwrittenCount > 0)
    {
        await db.SaveChangesAsync();
    }

    return Results.Ok(new ImportCatEtLicensesResult(
        importedCount,
        unchangedDuplicateCount,
        mismatchCount,
        overwrittenCount,
        invalidRows,
        duplicateSerialNumbers.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
        mismatchSerialNumbers.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
        overwrittenSerialNumbers.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList()));
});

app.MapPut("/api/catet/licenses/{id:int}", async (int id, UpdateCatEtLicenseRequest request, AppDbContext db) =>
{
    var license = await db.CatEtLicenses.FirstOrDefaultAsync(l => l.Id == id && l.DeletedAtUtc == null);
    if (license is null)
    {
        return Results.NotFound(new { message = "License not found." });
    }

    if (string.IsNullOrWhiteSpace(request.ActivationId))
    {
        return Results.BadRequest(new { message = "ActivationId is required." });
    }

    var activationId = NormalizeActivationId(request.ActivationId);
    if (!activationRegex.IsMatch(activationId))
    {
        return Results.BadRequest(new { message = "ActivationId must match xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xxxx (hex)." });
    }

    if (license.Status == CatEtLicenseStatus.Consumed)
    {
        return Results.BadRequest(new { message = "Consumed activation IDs cannot be edited directly. Use reset." });
    }

    TrackedComputer? computer = null;
    if (request.TrackedComputerId is int trackedComputerId)
    {
        computer = await db.TrackedComputers.Include(c => c.TrackedPerson).FirstOrDefaultAsync(c => c.Id == trackedComputerId);
        if (computer is null)
        {
            return Results.BadRequest(new { message = "TrackedComputerId does not exist." });
        }
    }

    license.ActivationId = activationId;
    license.LicenseKey = activationId;
    license.TrackedComputerId = request.TrackedComputerId;

    db.CatEtActivationEvents.Add(new CatEtActivationEvent
    {
        CatEtLicenseId = license.Id,
        EventType = "Edited",
        Notes = "Activation ID edited"
    });

    await db.SaveChangesAsync();

    return Results.Ok(new CatEtLicenseDto(
        license.Id,
        license.SerialNumber,
        license.ActivationId,
        license.Status.ToString(),
        license.ActivatedAtUtc,
        license.LastResetAtUtc,
        license.TrackedComputerId,
        computer?.Hostname,
        computer?.AssetTag,
        computer?.TrackedPersonId,
        computer?.TrackedPerson?.FullName,
        license.CreatedAtUtc));
});

app.MapPost("/api/catet/licenses/{id:int}/activate", async (int id, AppDbContext db) =>
{
    var license = await db.CatEtLicenses.FirstOrDefaultAsync(l => l.Id == id && l.DeletedAtUtc == null);
    if (license is null)
    {
        return Results.NotFound(new { message = "License not found." });
    }

    if (license.Status != CatEtLicenseStatus.Available)
    {
        return Results.BadRequest(new { message = "Activation ID is not available." });
    }

    if (license.TrackedComputerId is null)
    {
        return Results.BadRequest(new { message = "Assign this key to a computer before marking activated." });
    }

    var hasActiveComputer = await db.TrackedComputers.AnyAsync(c => c.Id == license.TrackedComputerId && c.DeletedAtUtc == null);
    if (!hasActiveComputer)
    {
        return Results.BadRequest(new { message = "Assigned computer is missing or deleted. Reassign before activating." });
    }

    if (string.IsNullOrWhiteSpace(license.ActivationId))
    {
        return Results.BadRequest(new { message = "Activation ID is empty. Set a new ID before marking activated." });
    }

    var consumedActivationId = license.ActivationId;
    license.Status = CatEtLicenseStatus.Consumed;
    license.ActivatedAtUtc = DateTime.UtcNow;

    db.CatEtActivationEvents.Add(new CatEtActivationEvent
    {
        CatEtLicenseId = license.Id,
        EventType = "Consumed",
        Notes = $"Activation ID consumed: {consumedActivationId}"
    });

    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/api/catet/licenses/{id:int}/reset", async (int id, ResetActivationRequest request, AppDbContext db) =>
{
    var license = await db.CatEtLicenses.FirstOrDefaultAsync(l => l.Id == id && l.DeletedAtUtc == null);
    if (license is null)
    {
        return Results.NotFound(new { message = "License not found." });
    }

    var previousActivationId = license.ActivationId;

    license.ActivationId = string.Empty;
    license.LicenseKey = $"CLEARED-{license.Id}-{Guid.NewGuid():N}";
    license.Status = CatEtLicenseStatus.Available;
    license.LastResetAtUtc = DateTime.UtcNow;
    license.ActivatedAtUtc = null;

    var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Activation reset" : request.Reason.Trim();

    db.CatEtActivationEvents.Add(new CatEtActivationEvent
    {
        CatEtLicenseId = license.Id,
        EventType = "Reset",
        Notes = string.IsNullOrWhiteSpace(previousActivationId)
            ? reason
            : $"{reason}. Previous Activation ID cleared: {previousActivationId}"
    });

    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapDelete("/api/catet/licenses/{id:int}", async (int id, AppDbContext db) =>
{
    var license = await db.CatEtLicenses.FirstOrDefaultAsync(l => l.Id == id && l.DeletedAtUtc == null);
    if (license is null)
    {
        return Results.NotFound(new { message = "License not found." });
    }

    license.DeletedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/admin/deleted-records", async (AppDbContext db) =>
{
    var people = await db.TrackedPeople
        .Where(x => x.DeletedAtUtc != null)
        .Select(x => new DeletedRecordDto(x.Id, "people", x.FullName, x.DeletedAtUtc))
        .ToListAsync();

    var computers = await db.TrackedComputers
        .Where(x => x.DeletedAtUtc != null)
        .Select(x => new DeletedRecordDto(x.Id, "computers", $"{x.Hostname} ({x.AssetTag})", x.DeletedAtUtc))
        .ToListAsync();

    var licenses = await db.CatEtLicenses
        .Where(x => x.DeletedAtUtc != null)
        .Select(x => new DeletedRecordDto(x.Id, "licenses", $"{x.SerialNumber} - {x.ActivationId}", x.DeletedAtUtc))
        .ToListAsync();

    return Results.Ok(people.Concat(computers).Concat(licenses)
        .OrderByDescending(x => x.DeletedAtUtc)
        .ToList());
});

app.MapDelete("/api/admin/people/{id:int}/hard-delete", async (int id, AppDbContext db) =>
{
    var person = await db.TrackedPeople.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc != null);
    if (person is null)
    {
        return Results.NotFound(new { message = "Deleted person not found." });
    }

    var hasAnyComputers = await db.TrackedComputers.AnyAsync(c => c.TrackedPersonId == id);
    if (hasAnyComputers)
    {
        return Results.BadRequest(new { message = "Cannot hard delete person while computers still reference them." });
    }

    var hasAnyAllowances = await db.CellPhoneAllowances.AnyAsync(x => x.TrackedPersonId == id);
    if (hasAnyAllowances)
    {
        return Results.BadRequest(new { message = "Cannot hard delete person while cell phone allowances still reference them." });
    }

    db.TrackedPeople.Remove(person);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapDelete("/api/admin/computers/{id:int}/hard-delete", async (int id, AppDbContext db) =>
{
    var computer = await db.TrackedComputers.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc != null);
    if (computer is null)
    {
        return Results.NotFound(new { message = "Deleted computer not found." });
    }

    var linkedLicenses = await db.CatEtLicenses.Where(l => l.TrackedComputerId == id).ToListAsync();
    foreach (var license in linkedLicenses)
    {
        license.TrackedComputerId = null;
    }

    db.TrackedComputers.Remove(computer);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapDelete("/api/admin/licenses/{id:int}/hard-delete", async (int id, AppDbContext db) =>
{
    var license = await db.CatEtLicenses.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc != null);
    if (license is null)
    {
        return Results.NotFound(new { message = "Deleted license not found." });
    }

    db.CatEtLicenses.Remove(license);
    await db.SaveChangesAsync();
    return Results.Ok();
});

void EnsureTrackedComputerPersonNullable(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var checkCommand = connection.CreateCommand();
    checkCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TrackedComputers') WHERE name = 'TrackedPersonId' AND \"notnull\" = 1;";
    var needsMigration = Convert.ToInt32(checkCommand.ExecuteScalar() ?? 0) > 0;

    if (!needsMigration)
    {
        return;
    }

    using var migrateCommand = connection.CreateCommand();
    migrateCommand.CommandText = @"
PRAGMA foreign_keys = OFF;

CREATE TABLE IF NOT EXISTS ""__TrackedComputers_new"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_TrackedComputers"" PRIMARY KEY AUTOINCREMENT,
    ""Hostname"" TEXT NOT NULL,
    ""AssetTag"" TEXT NOT NULL,
    ""TrackedPersonId"" INTEGER NULL,
    ""CreatedAtUtc"" TEXT NOT NULL,
    CONSTRAINT ""FK_TrackedComputers_TrackedPeople_TrackedPersonId""
        FOREIGN KEY (""TrackedPersonId"") REFERENCES ""TrackedPeople"" (""Id"") ON DELETE RESTRICT
);

INSERT INTO ""__TrackedComputers_new"" (""Id"", ""Hostname"", ""AssetTag"", ""TrackedPersonId"", ""CreatedAtUtc"")
SELECT ""Id"", ""Hostname"", ""AssetTag"", ""TrackedPersonId"", ""CreatedAtUtc"" FROM ""TrackedComputers"";

DROP TABLE ""TrackedComputers"";
ALTER TABLE ""__TrackedComputers_new"" RENAME TO ""TrackedComputers"";
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_TrackedComputers_AssetTag"" ON ""TrackedComputers"" (""AssetTag"");

PRAGMA foreign_keys = ON;";

    migrateCommand.ExecuteNonQuery();
}

void EnsureCatEtLicenseComputerNullable(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var nullabilityCheck = connection.CreateCommand();
    nullabilityCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CatEtLicenses') WHERE name = 'TrackedComputerId' AND \"notnull\" = 1;";
    var needsNullableMigration = Convert.ToInt32(nullabilityCheck.ExecuteScalar() ?? 0) > 0;

    if (needsNullableMigration)
    {
        using var migrateCommand = connection.CreateCommand();
        migrateCommand.CommandText = @"
PRAGMA foreign_keys = OFF;

CREATE TABLE IF NOT EXISTS ""__CatEtLicenses_new"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_CatEtLicenses"" PRIMARY KEY AUTOINCREMENT,
    ""LicenseKey"" TEXT NOT NULL,
    ""Status"" INTEGER NOT NULL,
    ""TrackedComputerId"" INTEGER NULL,
    ""CreatedAtUtc"" TEXT NOT NULL,
    CONSTRAINT ""FK_CatEtLicenses_TrackedComputers_TrackedComputerId""
        FOREIGN KEY (""TrackedComputerId"") REFERENCES ""TrackedComputers"" (""Id"") ON DELETE RESTRICT
);

INSERT INTO ""__CatEtLicenses_new"" (""Id"", ""LicenseKey"", ""Status"", ""TrackedComputerId"", ""CreatedAtUtc"")
SELECT ""Id"", ""LicenseKey"", ""Status"", ""TrackedComputerId"", ""CreatedAtUtc"" FROM ""CatEtLicenses"";

DROP TABLE ""CatEtLicenses"";
ALTER TABLE ""__CatEtLicenses_new"" RENAME TO ""CatEtLicenses"";
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CatEtLicenses_LicenseKey"" ON ""CatEtLicenses"" (""LicenseKey"");

PRAGMA foreign_keys = ON;";

        migrateCommand.ExecuteNonQuery();
    }

    using var ensureColumns = connection.CreateCommand();
    ensureColumns.CommandText = @"
ALTER TABLE ""CatEtLicenses"" ADD COLUMN ""SerialNumber"" TEXT NULL;
ALTER TABLE ""CatEtLicenses"" ADD COLUMN ""ActivationId"" TEXT NULL;
ALTER TABLE ""CatEtLicenses"" ADD COLUMN ""ActivatedAtUtc"" TEXT NULL;
ALTER TABLE ""CatEtLicenses"" ADD COLUMN ""LastResetAtUtc"" TEXT NULL;";

    try { ensureColumns.ExecuteNonQuery(); } catch { }

    using var backfill = connection.CreateCommand();
    backfill.CommandText = @"
UPDATE ""CatEtLicenses"" SET ""SerialNumber"" = COALESCE(NULLIF(""SerialNumber"", ''), 'SER-' || ""Id"");
UPDATE ""CatEtLicenses"" SET ""ActivationId"" = COALESCE(NULLIF(""ActivationId"", ''), COALESCE(""LicenseKey"", ''));
UPDATE ""CatEtLicenses"" SET ""Status"" = CASE WHEN ""Status"" = 0 THEN 0 WHEN ""Status"" = 1 THEN 1 ELSE 2 END;";
    backfill.ExecuteNonQuery();

    using var indexes = connection.CreateCommand();
    indexes.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CatEtLicenses_SerialNumber"" ON ""CatEtLicenses"" (""SerialNumber"");
CREATE TABLE IF NOT EXISTS ""CatEtActivationEvents"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_CatEtActivationEvents"" PRIMARY KEY AUTOINCREMENT,
    ""CatEtLicenseId"" INTEGER NOT NULL,
    ""EventType"" TEXT NOT NULL,
    ""Notes"" TEXT NULL,
    ""OccurredAtUtc"" TEXT NOT NULL,
    CONSTRAINT ""FK_CatEtActivationEvents_CatEtLicenses_CatEtLicenseId"" FOREIGN KEY (""CatEtLicenseId"") REFERENCES ""CatEtLicenses"" (""Id"") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ""IX_CatEtActivationEvents_CatEtLicenseId"" ON ""CatEtActivationEvents"" (""CatEtLicenseId"");";
    indexes.ExecuteNonQuery();
}

void EnsureSoftDeleteColumns(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var command = connection.CreateCommand();
    command.CommandText = @"
ALTER TABLE ""TrackedPeople"" ADD COLUMN ""DeletedAtUtc"" TEXT NULL;
ALTER TABLE ""TrackedComputers"" ADD COLUMN ""DeletedAtUtc"" TEXT NULL;
ALTER TABLE ""CatEtLicenses"" ADD COLUMN ""DeletedAtUtc"" TEXT NULL;";

    try { command.ExecuteNonQuery(); } catch { }
}

void EnsureTrackedPersonPhoneColumns(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var columnsCommand = connection.CreateCommand();
    columnsCommand.CommandText = "PRAGMA table_info('TrackedPeople');";
    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var reader = columnsCommand.ExecuteReader())
    {
        while (reader.Read())
        {
            existingColumns.Add(reader.GetString(1));
        }
    }

    if (!existingColumns.Contains("MobilePhone"))
    {
        using var addMobilePhone = connection.CreateCommand();
        addMobilePhone.CommandText = "ALTER TABLE \"TrackedPeople\" ADD COLUMN \"MobilePhone\" TEXT NULL;";
        addMobilePhone.ExecuteNonQuery();
    }

    if (!existingColumns.Contains("BusinessPhone"))
    {
        using var addBusinessPhone = connection.CreateCommand();
        addBusinessPhone.CommandText = "ALTER TABLE \"TrackedPeople\" ADD COLUMN \"BusinessPhone\" TEXT NULL;";
        addBusinessPhone.ExecuteNonQuery();
    }
}

void EnsureCellPhoneAllowanceSchema(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var command = connection.CreateCommand();
    command.CommandText = @"
CREATE TABLE IF NOT EXISTS ""CellPhoneAllowances"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_CellPhoneAllowances"" PRIMARY KEY AUTOINCREMENT,
    ""TrackedPersonId"" INTEGER NOT NULL,
    ""MobilePhoneNumber"" TEXT NOT NULL,
    ""AllowanceGranted"" INTEGER NOT NULL DEFAULT 0,
    ""ApprovedAtUtc"" TEXT NULL,
    ""CreatedAtUtc"" TEXT NOT NULL,
    ""DeletedAtUtc"" TEXT NULL,
    CONSTRAINT ""FK_CellPhoneAllowances_TrackedPeople_TrackedPersonId""
        FOREIGN KEY (""TrackedPersonId"") REFERENCES ""TrackedPeople"" (""Id"") ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ""IX_CellPhoneAllowances_TrackedPersonId"" ON ""CellPhoneAllowances"" (""TrackedPersonId"");";
    command.ExecuteNonQuery();
}

void EnsureTrackedComputerSyncControlColumns(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    if (!TableColumnExists(connection, "TrackedComputers", "ExcludeFromSync"))
    {
        using var addExclude = connection.CreateCommand();
        addExclude.CommandText = "ALTER TABLE \"TrackedComputers\" ADD COLUMN \"ExcludeFromSync\" INTEGER NOT NULL DEFAULT 0;";
        addExclude.ExecuteNonQuery();
    }

    if (!TableColumnExists(connection, "TrackedComputers", "HiddenFromTable"))
    {
        using var addHidden = connection.CreateCommand();
        addHidden.CommandText = "ALTER TABLE \"TrackedComputers\" ADD COLUMN \"HiddenFromTable\" INTEGER NOT NULL DEFAULT 0;";
        addHidden.ExecuteNonQuery();
    }

    if (!TableColumnExists(connection, "TrackedComputers", "IsMobileDevice"))
    {
        using var addMobile = connection.CreateCommand();
        addMobile.CommandText = "ALTER TABLE \"TrackedComputers\" ADD COLUMN \"IsMobileDevice\" INTEGER NOT NULL DEFAULT 0;";
        addMobile.ExecuteNonQuery();
    }

    if (!TableColumnExists(connection, "TrackedComputers", "AssetCategory"))
    {
        using var addCategory = connection.CreateCommand();
        addCategory.CommandText = "ALTER TABLE \"TrackedComputers\" ADD COLUMN \"AssetCategory\" TEXT NOT NULL DEFAULT 'Computer';";
        addCategory.ExecuteNonQuery();
    }

    using var normalizeCategory = connection.CreateCommand();
    normalizeCategory.CommandText = @"
UPDATE ""TrackedComputers""
SET ""AssetCategory"" = CASE
    WHEN ""AssetCategory"" IS NULL OR TRIM(""AssetCategory"") = '' THEN 'Computer'
    WHEN ""AssetCategory"" = 'Mobile Device' THEN 'Other Device'
    WHEN ""AssetCategory"" IN ('Computer', 'Phone', 'Tablet', 'Other Device') THEN ""AssetCategory""
    ELSE 'Other Device'
END;";
    normalizeCategory.ExecuteNonQuery();
}

void EnsureEntityResourceCoverageSchema(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var schemaCommand = connection.CreateCommand();
    schemaCommand.CommandText = @"
CREATE TABLE IF NOT EXISTS ""ResourceDefinitions"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ResourceDefinitions"" PRIMARY KEY AUTOINCREMENT,
    ""EntityType"" INTEGER NOT NULL,
    ""Provider"" TEXT NOT NULL,
    ""ResourceType"" TEXT NOT NULL,
    ""DisplayName"" TEXT NOT NULL,
    ""IsEnabled"" INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS ""EntityReferences"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_EntityReferences"" PRIMARY KEY AUTOINCREMENT,
    ""EntityType"" INTEGER NOT NULL,
    ""EntityId"" INTEGER NOT NULL,
    ""ResourceDefinitionId"" INTEGER NOT NULL,
    ""ExternalId"" TEXT NOT NULL,
    ""ExternalKey"" TEXT NULL,
    ""SyncStatus"" INTEGER NOT NULL,
    ""LastSyncedAtUtc"" TEXT NOT NULL,
    ""FirstLinkedAtUtc"" TEXT NULL,
    ""LastSeenAtUtc"" TEXT NULL,
    ""MetadataJson"" TEXT NULL,
    CONSTRAINT ""FK_EntityReferences_ResourceDefinitions_ResourceDefinitionId"" FOREIGN KEY (""ResourceDefinitionId"") REFERENCES ""ResourceDefinitions"" (""Id"") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ResourceDefinitions_EntityType_Provider_ResourceType""
    ON ""ResourceDefinitions"" (""EntityType"", ""Provider"", ""ResourceType"");

CREATE INDEX IF NOT EXISTS ""IX_EntityReferences_EntityType_EntityId""
    ON ""EntityReferences"" (""EntityType"", ""EntityId"");

CREATE INDEX IF NOT EXISTS ""IX_EntityReferences_ResourceDefinitionId_ExternalKey""
    ON ""EntityReferences"" (""ResourceDefinitionId"", ""ExternalKey"");

CREATE UNIQUE INDEX IF NOT EXISTS ""IX_EntityReferences_EntityType_EntityId_ResourceDefinitionId_ExternalId""
    ON ""EntityReferences"" (""EntityType"", ""EntityId"", ""ResourceDefinitionId"", ""ExternalId"");
";

    schemaCommand.ExecuteNonQuery();

    using var seedCommand = connection.CreateCommand();
    seedCommand.CommandText = @"
INSERT OR IGNORE INTO ""ResourceDefinitions"" (""EntityType"", ""Provider"", ""ResourceType"", ""DisplayName"", ""IsEnabled"") VALUES
(1, 'MicrosoftGraph', 'User', 'Microsoft 365 User', 1),
(2, 'NinjaRMM', 'Device', 'Ninja Device', 1),
(2, 'MicrosoftGraph', 'Device', 'Entra Device', 1),
(2, 'Intune', 'ManagedDevice', 'Intune Managed Device', 1),
(2, 'Azure', 'VirtualMachine', 'Azure VM', 1);
";
    seedCommand.ExecuteNonQuery();
}

void EnsureIntegrationProviderConfigSchema(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var command = connection.CreateCommand();
    command.CommandText = @"
CREATE TABLE IF NOT EXISTS ""IntegrationProviderConfigs"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_IntegrationProviderConfigs"" PRIMARY KEY AUTOINCREMENT,
    ""Provider"" TEXT NOT NULL,
    ""BaseUrl"" TEXT NULL,
    ""TenantId"" TEXT NULL,
    ""ClientId"" TEXT NULL,
    ""ClientSecret"" TEXT NULL,
    ""Scope"" TEXT NULL,
    ""TokenPath"" TEXT NULL,
    ""DevicesPath"" TEXT NULL,
    ""PageSize"" INTEGER NULL,
    ""ResourceManagerBaseUrl"" TEXT NULL,
    ""AzureSubscriptionIdsCsv"" TEXT NULL,
    ""UpdatedAtUtc"" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_IntegrationProviderConfigs_Provider"" ON ""IntegrationProviderConfigs"" (""Provider"");
";

    command.ExecuteNonQuery();
}

void EnsureIntegrationSyncStatusSchema(AppDbContext db)
{
    using var connection = db.Database.GetDbConnection();

    if (connection.State != ConnectionState.Open)
    {
        connection.Open();
    }

    using var command = connection.CreateCommand();
    command.CommandText = @"
CREATE TABLE IF NOT EXISTS ""IntegrationSyncStatuses"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_IntegrationSyncStatuses"" PRIMARY KEY AUTOINCREMENT,
    ""SyncTarget"" TEXT NOT NULL,
    ""IsRunning"" INTEGER NOT NULL DEFAULT 0,
    ""LastStatus"" TEXT NOT NULL,
    ""LastRunStartedAtUtc"" TEXT NULL,
    ""LastRunCompletedAtUtc"" TEXT NULL,
    ""LastSuccessAtUtc"" TEXT NULL,
    ""LastSeenCount"" INTEGER NOT NULL DEFAULT 0,
    ""LastMatchedCount"" INTEGER NOT NULL DEFAULT 0,
    ""LastMessage"" TEXT NULL,
    ""LastTriggeredBy"" TEXT NULL,
    ""LastDetailsJson"" TEXT NULL,
    ""UpdatedAtUtc"" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_IntegrationSyncStatuses_SyncTarget"" ON ""IntegrationSyncStatuses"" (""SyncTarget"");
";

    command.ExecuteNonQuery();

    if (!TableColumnExists(connection, "IntegrationSyncStatuses", "LastDetailsJson"))
    {
        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE \"IntegrationSyncStatuses\" ADD COLUMN \"LastDetailsJson\" TEXT NULL;";
        alterCommand.ExecuteNonQuery();
    }
}

bool TableColumnExists(DbConnection connection, string tableName, string columnName)
{
    using var pragma = connection.CreateCommand();
    pragma.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";

    using var reader = pragma.ExecuteReader();
    while (reader.Read())
    {
        var current = reader[1]?.ToString();
        if (string.Equals(current, columnName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

string? NormalizeAssetCategory(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    return raw.Trim().ToLowerInvariant() switch
    {
        "computer" => "Computer",
        "mobile" => "Other Device",
        "mobiledevice" => "Other Device",
        "mobile device" => "Other Device",
        "mobile devices" => "Other Device",
        "phone" => "Phone",
        "tablet" => "Tablet",
        "other" => "Other Device",
        "otherdevice" => "Other Device",
        "other device" => "Other Device",
        "all" => "all",
        _ => "Other Device"
    };
}

bool TryParseResourceEntityType(string value, out ResourceEntityType entityType)
{
    switch (value.Trim().ToLowerInvariant())
    {
        case "user":
        case "users":
            entityType = ResourceEntityType.User;
            return true;
        case "computer":
        case "computers":
            entityType = ResourceEntityType.Computer;
            return true;
        default:
            entityType = default;
            return false;
    }
}

ResourceEntityTypeDto ToDtoEntityType(ResourceEntityType entityType)
    => entityType == ResourceEntityType.User ? ResourceEntityTypeDto.User : ResourceEntityTypeDto.Computer;

ReferenceStatusDto ToDtoReferenceStatus(ReferenceSyncStatus status)
    => status switch
    {
        ReferenceSyncStatus.Linked => ReferenceStatusDto.Linked,
        ReferenceSyncStatus.Stale => ReferenceStatusDto.Stale,
        ReferenceSyncStatus.Missing => ReferenceStatusDto.Missing,
        ReferenceSyncStatus.Error => ReferenceStatusDto.Error,
        _ => ReferenceStatusDto.Error
    };

string NormalizeSerialNumber(string input)
    => new string(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

string NormalizeActivationId(string input)
{
    var hex = new string(input.ToLowerInvariant().Where(Uri.IsHexDigit).ToArray());
    if (hex.Length > 32)
    {
        hex = hex[..32];
    }

    var groups = new List<string>();
    for (var i = 0; i < hex.Length; i += 4)
    {
        var length = Math.Min(4, hex.Length - i);
        groups.Add(hex.Substring(i, length));
    }

    return string.Join('-', groups);
}

List<ImportRow> ParseImportRows(Stream stream, string extension)
    => extension == ".xlsx" ? ParseXlsxRows(stream) : ParseCsvRows(stream);

List<ImportRow> ParseXlsxRows(Stream stream)
{
    using var workbook = new XLWorkbook(stream);
    var worksheet = workbook.Worksheets.FirstOrDefault();
    if (worksheet is null)
    {
        return [];
    }

    var rows = new List<ImportRow>();
    var sawHeader = false;

    foreach (var row in worksheet.RowsUsed())
    {
        var serial = row.Cell(1).GetString().Trim();
        var activationId = row.Cell(2).GetString().Trim();

        if (!sawHeader && LooksLikeHeader(serial, activationId))
        {
            sawHeader = true;
            continue;
        }

        if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(activationId))
        {
            continue;
        }

        rows.Add(new ImportRow(row.RowNumber(), serial, activationId));
    }

    return rows;
}

List<ImportRow> ParseCsvRows(Stream stream)
{
    if (stream.CanSeek)
    {
        stream.Position = 0;
    }

    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

    var rows = new List<ImportRow>();
    var sawHeader = false;
    var lineNumber = 0;

    while (!reader.EndOfStream)
    {
        var line = reader.ReadLine();
        lineNumber++;

        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var cells = line.Split(',', 3, StringSplitOptions.TrimEntries);
        var serial = cells.Length > 0 ? cells[0].Trim('"', ' ') : string.Empty;
        var activationId = cells.Length > 1 ? cells[1].Trim('"', ' ') : string.Empty;

        if (!sawHeader && LooksLikeHeader(serial, activationId))
        {
            sawHeader = true;
            continue;
        }

        if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(activationId))
        {
            continue;
        }

        rows.Add(new ImportRow(lineNumber, serial, activationId));
    }

    return rows;
}

bool LooksLikeHeader(string serial, string activationId)
{
    var combined = $"{serial} {activationId}".ToLowerInvariant();
    return combined.Contains("serial") && combined.Contains("activation");
}

app.Run();

record ImportRow(int RowNumber, string SerialNumber, string ActivationId);
record LoginRequest(string Username, string Password);
record LoginResponse(string Token, string Username, string Role);
record UserDto(int Id, string Username, string Role, DateTime CreatedAtUtc);
