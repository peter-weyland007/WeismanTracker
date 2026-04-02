using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using api.Contracts;
using api.Data;
using api.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

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

app.MapGet("/api/catet/people", async (AppDbContext db) =>
{
    var people = await db.TrackedPeople
        .Where(p => p.DeletedAtUtc == null)
        .OrderBy(p => p.FullName)
        .Select(p => new TrackedPersonDto(p.Id, p.FullName, p.Email, p.CreatedAtUtc))
        .ToListAsync();

    return Results.Ok(people);
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

    return Results.Ok(new TrackedPersonDto(person.Id, person.FullName, person.Email, person.CreatedAtUtc));
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

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new { message = "Email already exists." });
    }

    return Results.Ok(new TrackedPersonDto(person.Id, person.FullName, person.Email, person.CreatedAtUtc));
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

    person.DeletedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/api/catet/computers", async (AppDbContext db) =>
{
    var computers = await db.TrackedComputers
        .Where(c => c.DeletedAtUtc == null)
        .Include(c => c.TrackedPerson)
        .OrderBy(c => c.Hostname)
        .Select(c => new TrackedComputerDto(
            c.Id,
            c.Hostname,
            c.AssetTag,
            c.TrackedPersonId,
            c.TrackedPerson != null ? c.TrackedPerson.FullName : null,
            c.CreatedAtUtc))
        .ToListAsync();

    return Results.Ok(computers);
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
        CreatedAtUtc = DateTime.UtcNow
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
        computer.CreatedAtUtc));
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
        computer.CreatedAtUtc));
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
