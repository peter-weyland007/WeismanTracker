using System.Text.Json;
using api.Contracts;
using api.Data;
using api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace api.Integrations;

public sealed class ResourceSyncBackgroundService : BackgroundService
{
    private const string NinjaTarget = "Ninja";
    private const string MicrosoftTarget = "Microsoft";

    private const string NinjaDevicesSource = "NinjaDevices";
    private const string GraphUsersSource = "GraphUsers";
    private const string GraphDevicesSource = "GraphDevices";
    private const string IntuneManagedDevicesSource = "IntuneManagedDevices";
    private const string AzureVirtualMachinesSource = "AzureVirtualMachines";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ResourceSyncBackgroundService> _logger;
    private readonly IntegrationSyncOptions _options;

    private readonly SemaphoreSlim _ninjaLock = new(1, 1);
    private readonly SemaphoreSlim _microsoftLock = new(1, 1);

    public ResourceSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<IntegrationSyncOptions> options,
        ILogger<ResourceSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunMicrosoftSyncAsync(triggeredBy: "startup", waitForLock: true, stoppingToken);
        await RunNinjaSyncAsync(triggeredBy: "startup", waitForLock: true, stoppingToken);

        var microsoftTimer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(_options.UserSyncMinutes, 5)));
        var ninjaTimer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(_options.ComputerSyncMinutes, 5)));

        var microsoftTask = Task.Run(async () =>
        {
            while (await microsoftTimer.WaitForNextTickAsync(stoppingToken))
            {
                await RunMicrosoftSyncAsync(triggeredBy: "scheduler", waitForLock: false, stoppingToken);
            }
        }, stoppingToken);

        var ninjaTask = Task.Run(async () =>
        {
            while (await ninjaTimer.WaitForNextTickAsync(stoppingToken))
            {
                await RunNinjaSyncAsync(triggeredBy: "scheduler", waitForLock: false, stoppingToken);
            }
        }, stoppingToken);

        await Task.WhenAll(microsoftTask, ninjaTask);
    }

    public Task<TriggerIntegrationSyncResponseDto> RunNinjaSyncNowAsync(CancellationToken cancellationToken)
        => RunNinjaSyncAsync(triggeredBy: "manual", waitForLock: false, cancellationToken);

    public Task<TriggerIntegrationSyncResponseDto> RunMicrosoftSyncNowAsync(CancellationToken cancellationToken)
        => RunMicrosoftSyncAsync(triggeredBy: "manual", waitForLock: false, cancellationToken);

    public async Task<IntegrationSyncStatusSnapshotDto> GetSyncStatusSnapshotAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.IntegrationSyncStatuses
            .AsNoTracking()
            .Where(x => x.SyncTarget == NinjaTarget || x.SyncTarget == MicrosoftTarget)
            .ToListAsync(cancellationToken);

        var byTarget = rows.ToDictionary(x => x.SyncTarget, StringComparer.OrdinalIgnoreCase);

        var ninja = ToStatusDto(byTarget.TryGetValue(NinjaTarget, out var ninjaRow) ? ninjaRow : null, NinjaTarget);
        var microsoft = ToStatusDto(byTarget.TryGetValue(MicrosoftTarget, out var microsoftRow) ? microsoftRow : null, MicrosoftTarget);

        return new IntegrationSyncStatusSnapshotDto(ninja, microsoft);
    }

    private async Task<TriggerIntegrationSyncResponseDto> RunNinjaSyncAsync(string triggeredBy, bool waitForLock, CancellationToken cancellationToken)
    {
        var (acquired, acquiredAtUtc) = await TryAcquireAsync(_ninjaLock, waitForLock, cancellationToken);
        if (!acquired)
        {
            return new TriggerIntegrationSyncResponseDto(
                Target: NinjaTarget,
                Success: false,
                Message: "Ninja sync is already running.",
                SeenCount: 0,
                MatchedCount: 0,
                StartedAtUtc: DateTime.UtcNow,
                CompletedAtUtc: DateTime.UtcNow);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ninja = scope.ServiceProvider.GetRequiredService<INinjaClient>();
            var matcher = scope.ServiceProvider.GetRequiredService<IReferenceMatchService>();

            var status = await MarkStatusRunningAsync(db, NinjaTarget, triggeredBy, acquiredAtUtc, cancellationToken);

            try
            {
                var sourceStatuses = new List<IntegrationSyncSourceStatusDto>();
                var seenCount = 0;
                var matchedCount = 0;
                var existingMatchedCount = 0;
                var createdCount = 0;

                var definition = await db.ResourceDefinitions
                    .FirstOrDefaultAsync(x =>
                        x.EntityType == ResourceEntityType.Computer
                        && x.Provider == "NinjaRMM"
                        && x.ResourceType == "Device",
                        cancellationToken);

                if (definition is null)
                {
                    sourceStatuses.Add(CreateSourceStatus(NinjaDevicesSource, "Skipped", 0, 0, 0, 0, "Resource definition for NinjaRMM/Device not found."));
                }
                else
                {
                    var devices = await ninja.GetDevicesAsync(cancellationToken);
                    seenCount = devices.Count;

                    foreach (var device in devices)
                    {
                        if (string.IsNullOrWhiteSpace(device.ExternalId))
                        {
                            continue;
                        }

                        var isMobileHint = IsMobileDeviceHint(device.Os, device.DeviceName, device.Hostname);
                        var mobileCategoryHint = InferMobileCategoryHint(device.Os, device.DeviceName, device.Hostname);
                        var matchedEntityId = await matcher.MatchComputerEntityIdFromNinjaAsync(device, cancellationToken);
                        int? entityId;
                        if (matchedEntityId is not null)
                        {
                            entityId = matchedEntityId;
                            existingMatchedCount++;
                        }
                        else
                        {
                            entityId = await GetOrCreateTrackedComputerAsync(
                                db,
                                provider: "NinjaRMM",
                                externalId: device.ExternalId,
                                preferredHostname: device.Hostname ?? device.DeviceName,
                                serialOrAsset: device.SerialNumber,
                                machineId: null,
                                isMobileHint: isMobileHint,
                                mobileCategoryHint: mobileCategoryHint,
                                cancellationToken);

                            if (entityId is not null)
                            {
                                createdCount++;
                            }
                        }

                        if (entityId is null)
                        {
                            continue;
                        }

                        if (await ShouldSkipComputerReferenceAsync(db, entityId.Value, isMobileHint, mobileCategoryHint, cancellationToken))
                        {
                            continue;
                        }

                        await UpsertReferenceAsync(db, ResourceEntityType.Computer, entityId.Value, definition.Id, device.ExternalId,
                            device.SerialNumber ?? device.Hostname,
                            new Dictionary<string, string?>
                            {
                                ["DeviceName"] = device.DeviceName,
                                ["Hostname"] = device.Hostname,
                                ["OS"] = device.Os
                            }, cancellationToken);

                        matchedCount++;
                    }

                    sourceStatuses.Add(CreateSourceStatus(NinjaDevicesSource, "Success", seenCount, matchedCount, existingMatchedCount, createdCount, null));
                }

                await db.SaveChangesAsync(cancellationToken);

                var success = sourceStatuses.All(x => !x.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));
                var message = BuildAggregateMessage(NinjaTarget, seenCount, matchedCount, sourceStatuses);
                await MarkStatusCompleteAsync(db, status, success, message, seenCount, matchedCount, sourceStatuses, cancellationToken);
                _logger.LogInformation(message);

                return ToRunResult(NinjaTarget, success, message, seenCount, matchedCount, acquiredAtUtc, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ninja sync failed");
                var message = ex.Message;
                var sourceStatuses = new[]
                {
                    CreateSourceStatus(NinjaDevicesSource, "Failed", 0, 0, 0, 0, message)
                };
                await MarkStatusCompleteAsync(db, status, false, message, 0, 0, sourceStatuses, cancellationToken);
                return ToRunResult(NinjaTarget, false, message, 0, 0, acquiredAtUtc, DateTime.UtcNow);
            }
        }
        finally
        {
            _ninjaLock.Release();
        }
    }

    private async Task<TriggerIntegrationSyncResponseDto> RunMicrosoftSyncAsync(string triggeredBy, bool waitForLock, CancellationToken cancellationToken)
    {
        var (acquired, acquiredAtUtc) = await TryAcquireAsync(_microsoftLock, waitForLock, cancellationToken);
        if (!acquired)
        {
            return new TriggerIntegrationSyncResponseDto(
                Target: MicrosoftTarget,
                Success: false,
                Message: "Microsoft sync is already running.",
                SeenCount: 0,
                MatchedCount: 0,
                StartedAtUtc: DateTime.UtcNow,
                CompletedAtUtc: DateTime.UtcNow);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var graph = scope.ServiceProvider.GetRequiredService<IMicrosoftGraphClient>();
            var matcher = scope.ServiceProvider.GetRequiredService<IReferenceMatchService>();

            var status = await MarkStatusRunningAsync(db, MicrosoftTarget, triggeredBy, acquiredAtUtc, cancellationToken);

            try
            {
                var definitions = await db.ResourceDefinitions
                    .Where(x =>
                        (x.EntityType == ResourceEntityType.User && x.Provider == "MicrosoftGraph" && x.ResourceType == "User")
                        || (x.EntityType == ResourceEntityType.Computer && x.Provider == "MicrosoftGraph" && x.ResourceType == "Device")
                        || (x.EntityType == ResourceEntityType.Computer && x.Provider == "Intune" && x.ResourceType == "ManagedDevice")
                        || (x.EntityType == ResourceEntityType.Computer && x.Provider == "Azure" && x.ResourceType == "VirtualMachine"))
                    .ToListAsync(cancellationToken);

                var userDefId = definitions.FirstOrDefault(x => x.EntityType == ResourceEntityType.User && x.Provider == "MicrosoftGraph" && x.ResourceType == "User")?.Id;
                var graphDeviceDefId = definitions.FirstOrDefault(x => x.EntityType == ResourceEntityType.Computer && x.Provider == "MicrosoftGraph" && x.ResourceType == "Device")?.Id;
                var intuneDefId = definitions.FirstOrDefault(x => x.EntityType == ResourceEntityType.Computer && x.Provider == "Intune" && x.ResourceType == "ManagedDevice")?.Id;
                var azureVmDefId = definitions.FirstOrDefault(x => x.EntityType == ResourceEntityType.Computer && x.Provider == "Azure" && x.ResourceType == "VirtualMachine")?.Id;

                var sourceStatuses = new List<IntegrationSyncSourceStatusDto>();
                var seenCount = 0;
                var matchedCount = 0;
                var existingMatchedCount = 0;
                var createdCount = 0;

                if (userDefId is null)
                {
                    sourceStatuses.Add(CreateSourceStatus(GraphUsersSource, "Skipped", 0, 0, 0, 0, "Resource definition for MicrosoftGraph/User not found."));
                }
                else
                {
                    var sourceMatchedCount = 0;
                    var sourceExistingMatchedCount = 0;
                    var sourceCreatedCount = 0;
                    try
                    {
                        var users = await graph.GetUsersAsync(cancellationToken);
                        var sourceSeenCount = users.Count;

                        foreach (var user in users)
                        {
                            if (string.IsNullOrWhiteSpace(user.ExternalId))
                            {
                                continue;
                            }

                            var matchedEntityId = await matcher.MatchUserEntityIdAsync(user, cancellationToken);
                            int? entityId;
                            if (matchedEntityId is not null)
                            {
                                entityId = matchedEntityId;
                                sourceExistingMatchedCount++;
                            }
                            else
                            {
                                entityId = await GetOrCreateTrackedPersonAsync(db, user, cancellationToken);
                                if (entityId is not null)
                                {
                                    sourceCreatedCount++;
                                }
                            }

                            if (entityId is null)
                            {
                                continue;
                            }

                            await UpsertReferenceAsync(db, ResourceEntityType.User, entityId.Value, userDefId.Value, user.ExternalId,
                                user.UserPrincipalName ?? user.Mail,
                                new Dictionary<string, string?>
                                {
                                    ["DisplayName"] = user.DisplayName,
                                    ["Mail"] = user.Mail,
                                    ["UPN"] = user.UserPrincipalName
                                }, cancellationToken);

                            sourceMatchedCount++;
                        }

                        seenCount += sourceSeenCount;
                        matchedCount += sourceMatchedCount;
                        existingMatchedCount += sourceExistingMatchedCount;
                        createdCount += sourceCreatedCount;
                        sourceStatuses.Add(CreateSourceStatus(GraphUsersSource, "Success", sourceSeenCount, sourceMatchedCount, sourceExistingMatchedCount, sourceCreatedCount, null));
                    }
                    catch (Exception ex)
                    {
                        sourceStatuses.Add(CreateSourceStatus(GraphUsersSource, "Failed", 0, 0, 0, 0, ex.Message));
                    }
                }

                if (graphDeviceDefId is null)
                {
                    sourceStatuses.Add(CreateSourceStatus(GraphDevicesSource, "Skipped", 0, 0, 0, 0, "Resource definition for MicrosoftGraph/Device not found."));
                }
                else
                {
                    var sourceMatchedCount = 0;
                    var sourceExistingMatchedCount = 0;
                    var sourceCreatedCount = 0;
                    try
                    {
                        var devices = await graph.GetDevicesAsync(cancellationToken);
                        var sourceSeenCount = devices.Count;

                        foreach (var device in devices)
                        {
                            if (string.IsNullOrWhiteSpace(device.ExternalId))
                            {
                                continue;
                            }

                            var isMobileHint = IsMobileDeviceHint(device.DisplayName);
                            var mobileCategoryHint = InferMobileCategoryHint(device.DisplayName);
                            var matchedEntityId = await matcher.MatchComputerEntityIdFromGraphDeviceAsync(device, cancellationToken);
                            int? entityId;
                            if (matchedEntityId is not null)
                            {
                                entityId = matchedEntityId;
                                sourceExistingMatchedCount++;
                            }
                            else
                            {
                                entityId = await GetOrCreateTrackedComputerAsync(
                                    db,
                                    provider: "MicrosoftGraph",
                                    externalId: device.ExternalId,
                                    preferredHostname: device.DisplayName,
                                    serialOrAsset: device.SerialNumber,
                                    machineId: device.DeviceId,
                                    isMobileHint: isMobileHint,
                                    mobileCategoryHint: mobileCategoryHint,
                                    cancellationToken);

                                if (entityId is not null)
                                {
                                    sourceCreatedCount++;
                                }
                            }

                            if (entityId is null)
                            {
                                continue;
                            }

                            if (await ShouldSkipComputerReferenceAsync(db, entityId.Value, isMobileHint, mobileCategoryHint, cancellationToken))
                            {
                                continue;
                            }

                            await UpsertReferenceAsync(db, ResourceEntityType.Computer, entityId.Value, graphDeviceDefId.Value, device.ExternalId,
                                device.SerialNumber ?? device.DeviceId,
                                new Dictionary<string, string?>
                                {
                                    ["DisplayName"] = device.DisplayName,
                                    ["DeviceId"] = device.DeviceId
                                }, cancellationToken);

                            sourceMatchedCount++;
                        }

                        seenCount += sourceSeenCount;
                        matchedCount += sourceMatchedCount;
                        existingMatchedCount += sourceExistingMatchedCount;
                        createdCount += sourceCreatedCount;
                        sourceStatuses.Add(CreateSourceStatus(GraphDevicesSource, "Success", sourceSeenCount, sourceMatchedCount, sourceExistingMatchedCount, sourceCreatedCount, null));
                    }
                    catch (Exception ex)
                    {
                        sourceStatuses.Add(CreateSourceStatus(GraphDevicesSource, "Failed", 0, 0, 0, 0, ex.Message));
                    }
                }

                if (intuneDefId is null)
                {
                    sourceStatuses.Add(CreateSourceStatus(IntuneManagedDevicesSource, "Skipped", 0, 0, 0, 0, "Resource definition for Intune/ManagedDevice not found."));
                }
                else
                {
                    var sourceMatchedCount = 0;
                    var sourceExistingMatchedCount = 0;
                    var sourceCreatedCount = 0;
                    try
                    {
                        var managedDevices = await graph.GetManagedDevicesAsync(cancellationToken);
                        var sourceSeenCount = managedDevices.Count;

                        foreach (var device in managedDevices)
                        {
                            if (string.IsNullOrWhiteSpace(device.ExternalId))
                            {
                                continue;
                            }

                            var isMobileHint = IsMobileDeviceHint(device.DeviceName);
                            var mobileCategoryHint = InferMobileCategoryHint(device.DeviceName);
                            var matchedEntityId = await matcher.MatchComputerEntityIdFromIntuneAsync(device, cancellationToken);
                            int? entityId;
                            if (matchedEntityId is not null)
                            {
                                entityId = matchedEntityId;
                                sourceExistingMatchedCount++;
                            }
                            else
                            {
                                entityId = await GetOrCreateTrackedComputerAsync(
                                    db,
                                    provider: "Intune",
                                    externalId: device.ExternalId,
                                    preferredHostname: device.DeviceName,
                                    serialOrAsset: device.SerialNumber,
                                    machineId: device.AzureAdDeviceId,
                                    isMobileHint: isMobileHint,
                                    mobileCategoryHint: mobileCategoryHint,
                                    cancellationToken);

                                if (entityId is not null)
                                {
                                    sourceCreatedCount++;
                                }
                            }

                            if (entityId is null)
                            {
                                continue;
                            }

                            if (await ShouldSkipComputerReferenceAsync(db, entityId.Value, isMobileHint, mobileCategoryHint, cancellationToken))
                            {
                                continue;
                            }

                            await UpsertReferenceAsync(db, ResourceEntityType.Computer, entityId.Value, intuneDefId.Value, device.ExternalId,
                                device.SerialNumber ?? device.AzureAdDeviceId,
                                new Dictionary<string, string?>
                                {
                                    ["DeviceName"] = device.DeviceName,
                                    ["AzureAdDeviceId"] = device.AzureAdDeviceId
                                }, cancellationToken);

                            sourceMatchedCount++;
                        }

                        seenCount += sourceSeenCount;
                        matchedCount += sourceMatchedCount;
                        existingMatchedCount += sourceExistingMatchedCount;
                        createdCount += sourceCreatedCount;
                        sourceStatuses.Add(CreateSourceStatus(IntuneManagedDevicesSource, "Success", sourceSeenCount, sourceMatchedCount, sourceExistingMatchedCount, sourceCreatedCount, null));
                    }
                    catch (Exception ex)
                    {
                        sourceStatuses.Add(CreateSourceStatus(IntuneManagedDevicesSource, "Failed", 0, 0, 0, 0, ex.Message));
                    }
                }

                if (azureVmDefId is null)
                {
                    sourceStatuses.Add(CreateSourceStatus(AzureVirtualMachinesSource, "Skipped", 0, 0, 0, 0, "Resource definition for Azure/VirtualMachine not found."));
                }
                else
                {
                    var sourceMatchedCount = 0;
                    var sourceExistingMatchedCount = 0;
                    var sourceCreatedCount = 0;
                    try
                    {
                        var vms = await graph.GetVirtualMachinesAsync(cancellationToken);
                        var sourceSeenCount = vms.Count;

                        foreach (var vm in vms)
                        {
                            if (string.IsNullOrWhiteSpace(vm.ExternalId))
                            {
                                continue;
                            }

                            var isMobileHint = IsMobileDeviceHint(vm.Name);
                            var mobileCategoryHint = InferMobileCategoryHint(vm.Name);
                            var matchedEntityId = await matcher.MatchComputerEntityIdFromAzureVmAsync(vm, cancellationToken);
                            int? entityId;
                            if (matchedEntityId is not null)
                            {
                                entityId = matchedEntityId;
                                sourceExistingMatchedCount++;
                            }
                            else
                            {
                                entityId = await GetOrCreateTrackedComputerAsync(
                                    db,
                                    provider: "Azure",
                                    externalId: vm.ExternalId,
                                    preferredHostname: vm.Name,
                                    serialOrAsset: vm.VmId,
                                    machineId: vm.VmId,
                                    isMobileHint: isMobileHint,
                                    mobileCategoryHint: mobileCategoryHint,
                                    cancellationToken);

                                if (entityId is not null)
                                {
                                    sourceCreatedCount++;
                                }
                            }

                            if (entityId is null)
                            {
                                continue;
                            }

                            if (await ShouldSkipComputerReferenceAsync(db, entityId.Value, isMobileHint, mobileCategoryHint, cancellationToken))
                            {
                                continue;
                            }

                            await UpsertReferenceAsync(db, ResourceEntityType.Computer, entityId.Value, azureVmDefId.Value, vm.ExternalId,
                                vm.VmId ?? vm.Name,
                                new Dictionary<string, string?>
                                {
                                    ["Name"] = vm.Name,
                                    ["VmId"] = vm.VmId
                                }, cancellationToken);

                            sourceMatchedCount++;
                        }

                        seenCount += sourceSeenCount;
                        matchedCount += sourceMatchedCount;
                        existingMatchedCount += sourceExistingMatchedCount;
                        createdCount += sourceCreatedCount;
                        sourceStatuses.Add(CreateSourceStatus(AzureVirtualMachinesSource, "Success", sourceSeenCount, sourceMatchedCount, sourceExistingMatchedCount, sourceCreatedCount, null));
                    }
                    catch (Exception ex)
                    {
                        sourceStatuses.Add(CreateSourceStatus(AzureVirtualMachinesSource, "Failed", 0, 0, 0, 0, ex.Message));
                    }
                }

                await db.SaveChangesAsync(cancellationToken);

                var success = sourceStatuses.All(x => !x.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));
                var message = BuildAggregateMessage(MicrosoftTarget, seenCount, matchedCount, sourceStatuses);
                await MarkStatusCompleteAsync(db, status, success, message, seenCount, matchedCount, sourceStatuses, cancellationToken);
                _logger.LogInformation(message);

                return ToRunResult(MicrosoftTarget, success, message, seenCount, matchedCount, acquiredAtUtc, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Microsoft sync failed");
                var message = ex.Message;
                var sourceStatuses = new[]
                {
                    CreateSourceStatus(GraphUsersSource, "Failed", 0, 0, 0, 0, message),
                    CreateSourceStatus(GraphDevicesSource, "Failed", 0, 0, 0, 0, message),
                    CreateSourceStatus(IntuneManagedDevicesSource, "Failed", 0, 0, 0, 0, message),
                    CreateSourceStatus(AzureVirtualMachinesSource, "Failed", 0, 0, 0, 0, message)
                };
                await MarkStatusCompleteAsync(db, status, false, message, 0, 0, sourceStatuses, cancellationToken);
                return ToRunResult(MicrosoftTarget, false, message, 0, 0, acquiredAtUtc, DateTime.UtcNow);
            }
        }
        finally
        {
            _microsoftLock.Release();
        }
    }

    private async Task<int?> GetOrCreateTrackedPersonAsync(AppDbContext db, GraphUserDto user, CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(user.Mail) ?? NormalizeEmail(user.UserPrincipalName);
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return null;
        }

        var existing = await db.TrackedPeople
            .FirstOrDefaultAsync(x => x.Email != null && x.Email.ToLower() == normalizedEmail, cancellationToken);

        if (existing is not null)
        {
            if (existing.DeletedAtUtc is not null)
            {
                existing.DeletedAtUtc = null;
            }

            if (string.IsNullOrWhiteSpace(existing.FullName) && !string.IsNullOrWhiteSpace(user.DisplayName))
            {
                existing.FullName = user.DisplayName.Trim();
            }

            return existing.Id;
        }

        var fullName = !string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.DisplayName.Trim()
            : normalizedEmail;

        var person = new TrackedPerson
        {
            FullName = fullName.Length > 120 ? fullName[..120] : fullName,
            Email = normalizedEmail
        };

        db.TrackedPeople.Add(person);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Auto-created tracked person from Microsoft sync: {Email}", normalizedEmail);
        return person.Id;
    }

    private async Task<int?> GetOrCreateTrackedComputerAsync(
        AppDbContext db,
        string provider,
        string externalId,
        string? preferredHostname,
        string? serialOrAsset,
        string? machineId,
        bool isMobileHint,
        string? mobileCategoryHint,
        CancellationToken cancellationToken)
    {
        var normalizedAssetCandidates = new[] { serialOrAsset, machineId }
            .Select(NormalizeAssetTag)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var candidate in normalizedAssetCandidates)
        {
            var existingByAsset = await db.TrackedComputers.FirstOrDefaultAsync(x => x.AssetTag.ToUpper() == candidate, cancellationToken);
            if (existingByAsset is not null)
            {
                if (existingByAsset.DeletedAtUtc is not null)
                {
                    existingByAsset.DeletedAtUtc = null;
                }

                if (string.IsNullOrWhiteSpace(existingByAsset.Hostname) && !string.IsNullOrWhiteSpace(preferredHostname))
                {
                    existingByAsset.Hostname = CleanHostname(preferredHostname);
                }

                if (isMobileHint)
                {
                    if (!existingByAsset.IsMobileDevice)
                    {
                        existingByAsset.IsMobileDevice = true;
                    }

                    if (string.IsNullOrWhiteSpace(existingByAsset.AssetCategory)
                        || existingByAsset.AssetCategory.Equals("Computer", StringComparison.OrdinalIgnoreCase)
                        || existingByAsset.AssetCategory.Equals("Mobile Device", StringComparison.OrdinalIgnoreCase))
                    {
                        existingByAsset.AssetCategory = mobileCategoryHint ?? "Other Device";
                    }
                }

                return existingByAsset.Id;
            }
        }

        var hostname = CleanHostname(preferredHostname);
        if (string.IsNullOrWhiteSpace(hostname))
        {
            hostname = $"{provider}-host-{externalId}";
        }

        var proposedAsset = normalizedAssetCandidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(proposedAsset))
        {
            proposedAsset = NormalizeAssetTag($"AUTO-{provider}-{externalId}") ?? string.Empty;
        }

        var uniqueAsset = await EnsureUniqueAssetTagAsync(db, proposedAsset, cancellationToken);
        var computer = new TrackedComputer
        {
            Hostname = hostname.Length > 120 ? hostname[..120] : hostname,
            AssetTag = uniqueAsset,
            IsMobileDevice = isMobileHint,
            AssetCategory = isMobileHint ? (mobileCategoryHint ?? "Other Device") : "Computer"
        };

        db.TrackedComputers.Add(computer);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Auto-created tracked computer from {Provider} sync: {AssetTag} / {Hostname}", provider, computer.AssetTag, computer.Hostname);
        return computer.Id;
    }

    private static string? NormalizeEmail(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string CleanHostname(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    private static bool IsMobileDeviceHint(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized.Contains("iphone") || normalized.Contains("ipad") || normalized.Contains("ios") || normalized.Contains("ipados") || normalized.Contains("android"))
            {
                return true;
            }
        }

        return false;
    }

    private static string? InferMobileCategoryHint(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized.Contains("ipad") || normalized.Contains("tablet"))
            {
                return "Tablet";
            }

            if (normalized.Contains("iphone") || normalized.Contains("phone"))
            {
                return "Phone";
            }

            if (normalized.Contains("android") || normalized.Contains("ios") || normalized.Contains("ipados"))
            {
                return "Other Device";
            }
        }

        return null;
    }

    private static async Task<bool> ShouldSkipComputerReferenceAsync(AppDbContext db, int entityId, bool isMobileHint, string? mobileCategoryHint, CancellationToken cancellationToken)
    {
        var computer = await db.TrackedComputers.FirstOrDefaultAsync(x => x.Id == entityId, cancellationToken);
        if (computer is null)
        {
            return false;
        }

        if (isMobileHint)
        {
            if (!computer.IsMobileDevice)
            {
                computer.IsMobileDevice = true;
            }

            if (string.IsNullOrWhiteSpace(computer.AssetCategory)
                || computer.AssetCategory.Equals("Computer", StringComparison.OrdinalIgnoreCase)
                || computer.AssetCategory.Equals("Mobile Device", StringComparison.OrdinalIgnoreCase))
            {
                computer.AssetCategory = mobileCategoryHint ?? "Other Device";
            }
        }

        return computer.ExcludeFromSync;
    }

    private static string? NormalizeAssetTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length > 120 ? normalized[..120] : normalized;
    }

    private static async Task<string> EnsureUniqueAssetTagAsync(AppDbContext db, string baseAssetTag, CancellationToken cancellationToken)
    {
        var seed = string.IsNullOrWhiteSpace(baseAssetTag) ? "AUTOASSET" : baseAssetTag;
        var candidate = seed.Length > 120 ? seed[..120] : seed;

        var suffix = 1;
        while (await db.TrackedComputers.AnyAsync(x => x.AssetTag.ToUpper() == candidate, cancellationToken))
        {
            var suffixText = $"-{suffix++}";
            var maxRootLen = Math.Max(1, 120 - suffixText.Length);
            candidate = (seed.Length > maxRootLen ? seed[..maxRootLen] : seed) + suffixText;
        }

        return candidate;
    }

    private static async Task<(bool Acquired, DateTime AcquiredAtUtc)> TryAcquireAsync(SemaphoreSlim semaphore, bool waitForLock, CancellationToken cancellationToken)
    {
        if (waitForLock)
        {
            await semaphore.WaitAsync(cancellationToken);
            return (true, DateTime.UtcNow);
        }

        var acquired = await semaphore.WaitAsync(0, cancellationToken);
        return (acquired, DateTime.UtcNow);
    }

    private static TriggerIntegrationSyncResponseDto ToRunResult(
        string target,
        bool success,
        string message,
        int seenCount,
        int matchedCount,
        DateTime startedAtUtc,
        DateTime completedAtUtc)
        => new(
            Target: target,
            Success: success,
            Message: message,
            SeenCount: seenCount,
            MatchedCount: matchedCount,
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: completedAtUtc);

    private static IntegrationSyncStatusDto ToStatusDto(IntegrationSyncStatus? row, string target)
        => row is null
            ? new IntegrationSyncStatusDto(
                Target: target,
                IsRunning: false,
                LastStatus: "Never",
                LastRunStartedAtUtc: null,
                LastRunCompletedAtUtc: null,
                LastSuccessAtUtc: null,
                LastSeenCount: 0,
                LastMatchedCount: 0,
                LastMessage: null,
                LastTriggeredBy: null,
                Sources: [])
            : new IntegrationSyncStatusDto(
                Target: row.SyncTarget,
                IsRunning: row.IsRunning,
                LastStatus: row.LastStatus,
                LastRunStartedAtUtc: row.LastRunStartedAtUtc,
                LastRunCompletedAtUtc: row.LastRunCompletedAtUtc,
                LastSuccessAtUtc: row.LastSuccessAtUtc,
                LastSeenCount: row.LastSeenCount,
                LastMatchedCount: row.LastMatchedCount,
                LastMessage: row.LastMessage,
                LastTriggeredBy: row.LastTriggeredBy,
                Sources: DeserializeSources(row.LastDetailsJson));

    private static async Task<IntegrationSyncStatus> MarkStatusRunningAsync(
        AppDbContext db,
        string target,
        string triggeredBy,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        var row = await db.IntegrationSyncStatuses.FirstOrDefaultAsync(x => x.SyncTarget == target, cancellationToken);
        if (row is null)
        {
            row = new IntegrationSyncStatus
            {
                SyncTarget = target,
                LastStatus = "Running",
                IsRunning = true,
                LastRunStartedAtUtc = startedAtUtc,
                LastTriggeredBy = triggeredBy,
                LastDetailsJson = null,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.IntegrationSyncStatuses.Add(row);
        }
        else
        {
            row.IsRunning = true;
            row.LastStatus = "Running";
            row.LastRunStartedAtUtc = startedAtUtc;
            row.LastTriggeredBy = triggeredBy;
            row.LastDetailsJson = null;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    private static async Task MarkStatusCompleteAsync(
        AppDbContext db,
        IntegrationSyncStatus row,
        bool success,
        string? message,
        int seenCount,
        int matchedCount,
        IReadOnlyList<IntegrationSyncSourceStatusDto> sourceStatuses,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        row.IsRunning = false;
        row.LastStatus = success ? "Success" : "Failed";
        row.LastRunCompletedAtUtc = now;
        if (success)
        {
            row.LastSuccessAtUtc = now;
        }

        row.LastSeenCount = seenCount;
        row.LastMatchedCount = matchedCount;
        row.LastMessage = string.IsNullOrWhiteSpace(message) ? null : message;
        row.LastDetailsJson = SerializeSources(sourceStatuses);
        row.UpdatedAtUtc = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    private static IntegrationSyncSourceStatusDto CreateSourceStatus(
        string source,
        string status,
        int seenCount,
        int matchedCount,
        int existingMatchedCount,
        int createdCount,
        string? message)
        => new(
            Source: source,
            Status: status,
            SeenCount: seenCount,
            MatchedCount: matchedCount,
            ExistingMatchedCount: existingMatchedCount,
            CreatedCount: createdCount,
            UnmatchedCount: Math.Max(0, seenCount - matchedCount),
            Message: string.IsNullOrWhiteSpace(message) ? null : message.Trim());

    private static string BuildAggregateMessage(string target, int seenCount, int matchedCount, IReadOnlyList<IntegrationSyncSourceStatusDto> sourceStatuses)
    {
        var failed = sourceStatuses.Where(x => x.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)).Select(x => x.Source).ToList();
        var skipped = sourceStatuses.Where(x => x.Status.Equals("Skipped", StringComparison.OrdinalIgnoreCase)).Select(x => x.Source).ToList();

        var existingMatched = sourceStatuses.Sum(x => x.ExistingMatchedCount);
        var created = sourceStatuses.Sum(x => x.CreatedCount);
        var unmatched = Math.Max(0, seenCount - matchedCount);

        var summary = $"{target} sync complete. Seen {seenCount}, matched {matchedCount} (existing {existingMatched}, created {created}, unmatched {unmatched}).";

        var userSources = sourceStatuses.Where(IsUserSource).ToList();
        var computerSources = sourceStatuses.Where(x => !IsUserSource(x)).ToList();

        if (userSources.Count > 0)
        {
            summary += $" Users: {BuildEntitySummary(userSources)}.";
        }

        if (computerSources.Count > 0)
        {
            summary += $" Computers: {BuildEntitySummary(computerSources)}.";
        }

        if (failed.Count > 0)
        {
            summary += $" Failed sources: {string.Join(", ", failed)}.";
        }

        if (skipped.Count > 0)
        {
            summary += $" Skipped sources: {string.Join(", ", skipped)}.";
        }

        return summary;
    }

    private static bool IsUserSource(IntegrationSyncSourceStatusDto source)
        => source.Source.Equals(GraphUsersSource, StringComparison.OrdinalIgnoreCase);

    private static string BuildEntitySummary(IReadOnlyList<IntegrationSyncSourceStatusDto> sources)
    {
        var seen = sources.Sum(x => x.SeenCount);
        var matched = sources.Sum(x => x.MatchedCount);
        var existing = sources.Sum(x => x.ExistingMatchedCount);
        var created = sources.Sum(x => x.CreatedCount);
        var unmatched = sources.Sum(x => x.UnmatchedCount);

        return $"seen {seen}, matched {matched} (existing {existing}, created {created}, unmatched {unmatched})";
    }

    private static string? SerializeSources(IReadOnlyList<IntegrationSyncSourceStatusDto> sourceStatuses)
        => sourceStatuses.Count == 0 ? null : JsonSerializer.Serialize(sourceStatuses);

    private static IReadOnlyList<IntegrationSyncSourceStatusDto> DeserializeSources(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<IntegrationSyncSourceStatusDto>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task UpsertReferenceAsync(
        AppDbContext db,
        ResourceEntityType entityType,
        int entityId,
        int resourceDefinitionId,
        string externalId,
        string? externalKey,
        Dictionary<string, string?> metadata,
        CancellationToken cancellationToken)
    {
        var existing = await db.EntityReferences.FirstOrDefaultAsync(x =>
            x.EntityType == entityType
            && x.EntityId == entityId
            && x.ResourceDefinitionId == resourceDefinitionId
            && x.ExternalId == externalId,
            cancellationToken);

        if (existing is null)
        {
            db.EntityReferences.Add(new EntityReference
            {
                EntityType = entityType,
                EntityId = entityId,
                ResourceDefinitionId = resourceDefinitionId,
                ExternalId = externalId,
                ExternalKey = externalKey,
                SyncStatus = ReferenceSyncStatus.Linked,
                FirstLinkedAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
                LastSyncedAtUtc = DateTime.UtcNow,
                MetadataJson = JsonSerializer.Serialize(metadata)
            });
            return;
        }

        existing.ExternalKey = externalKey;
        existing.SyncStatus = ReferenceSyncStatus.Linked;
        existing.LastSeenAtUtc = DateTime.UtcNow;
        existing.LastSyncedAtUtc = DateTime.UtcNow;
        existing.MetadataJson = JsonSerializer.Serialize(metadata);
    }
}
