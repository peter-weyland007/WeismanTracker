namespace api.Contracts;

public record TrackedPersonDto(int Id, string FullName, string? Email, string? EmployeeNumber, int? PayrollGroup, string? PayrollGroupLabel, string? MobilePhone, string? BusinessPhone, bool IsServiceAccount, DateTime CreatedAtUtc);
public record CreateTrackedPersonRequest(string FullName, string? Email, string? EmployeeNumber, int? PayrollGroup, string? MobilePhone, string? BusinessPhone, bool IsServiceAccount);

public record CellPhoneAllowanceDto(
    int Id,
    int TrackedPersonId,
    string TrackedPersonName,
    string? TrackedPersonEmail,
    string? TrackedPersonEmployeeNumber,
    int? TrackedPersonPayrollGroup,
    string? TrackedPersonPayrollGroupLabel,
    string MobilePhoneNumber,
    bool AllowanceGranted,
    DateTime? ApprovedAtUtc,
    DateTime CreatedAtUtc);

public record CreateCellPhoneAllowanceRequest(
    int TrackedPersonId,
    string MobilePhoneNumber,
    bool AllowanceGranted,
    DateTime? ApprovedAtUtc);

public record TrackedComputerDto(
    int Id,
    string Hostname,
    string? Alias,
    string AssetTag,
    string? SerialNumber,
    string? ComputerVariant,
    int? TrackedPersonId,
    string? TrackedPersonName,
    DateTime CreatedAtUtc,
    bool ExcludeFromSync,
    bool HiddenFromTable,
    bool IsMobileDevice,
    string AssetCategory);
public record CreateTrackedComputerRequest(string Hostname, string? Alias, string AssetTag, int? TrackedPersonId, string? SerialNumber = null, string? ComputerVariant = null, string? AssetCategory = null);
public record UpdateTrackedComputerFlagsRequest(bool? ExcludeFromSync, bool? HiddenFromTable, string? AssetCategory);

public record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public record CatEtLicenseDto(
    int Id,
    string SerialNumber,
    string ActivationId,
    string Status,
    DateTime? ActivatedAtUtc,
    DateTime? LastResetAtUtc,
    int? TrackedComputerId,
    string? Hostname,
    string? ComputerAlias,
    string? AssetTag,
    int? TrackedPersonId,
    string? TrackedPersonName,
    DateTime CreatedAtUtc);

public record CreateCatEtLicenseRequest(string SerialNumber, string ActivationId, int? TrackedComputerId);
public record UpdateCatEtLicenseRequest(string ActivationId, int? TrackedComputerId);
public record ResetActivationRequest(string? NewActivationId, string? Reason);

public record ImportCatEtLicensesResult(
    int ImportedCount,
    int UnchangedDuplicateCount,
    int MismatchCount,
    int OverwrittenCount,
    List<string> InvalidRows,
    List<string> DuplicateSerialNumbers,
    List<string> MismatchSerialNumbers,
    List<string> OverwrittenSerialNumbers);

public record DeletedRecordDto(int Id, string RecordType, string DisplayName, DateTime? DeletedAtUtc);
public record CatEtActivationEventDto(int Id, int CatEtLicenseId, string EventType, string? Notes, DateTime OccurredAtUtc);
public record CatEtActivationActivityRowDto(
    int EventId,
    int CatEtLicenseId,
    string SerialNumber,
    string EventType,
    string? Notes,
    DateTime OccurredAtUtc,
    string? Hostname,
    string? ComputerAlias,
    string? AssetTag,
    string? TrackedPersonName);

public enum ResourceEntityTypeDto
{
    User = 1,
    Computer = 2
}

public enum ReferenceStatusDto
{
    Linked = 1,
    Stale = 2,
    Missing = 3,
    Error = 4
}

public record ResourceDefinitionDto(
    int Id,
    ResourceEntityTypeDto EntityType,
    string Provider,
    string ResourceType,
    string DisplayName,
    bool IsEnabled);

public record ResourceCoverageRowDto(
    int ResourceDefinitionId,
    string Provider,
    string ResourceType,
    string DisplayName,
    bool Possible,
    bool Linked,
    string? ExternalId,
    string? ExternalKey,
    ReferenceStatusDto? SyncStatus,
    DateTime? LastSyncedAtUtc);

public record GetEntityResourceCoverageResponseDto(
    ResourceEntityTypeDto EntityType,
    int EntityId,
    IReadOnlyList<ResourceCoverageRowDto> Rows);

public record ReconcileEntityReferencesRequestDto(
    bool MarkMissingAsStale,
    IReadOnlyList<ReferenceUpsertDto>? Upserts,
    IReadOnlyList<ReferenceRemoveDto>? Removes);

public record ReferenceUpsertDto(
    int ResourceDefinitionId,
    string ExternalId,
    string? ExternalKey,
    Dictionary<string, string>? Metadata = null);

public record ReferenceRemoveDto(
    int ResourceDefinitionId,
    string ExternalId);

public record ReconcileEntityReferencesResponseDto(
    int Upserted,
    int Removed,
    int MarkedStale,
    IReadOnlyList<string> Warnings);

public record NinjaIntegrationConfigDto(
    string BaseUrl,
    string ClientId,
    bool HasClientSecret,
    string Scope,
    string TokenPath,
    string DevicesPath,
    int PageSize);

public record MicrosoftGraphIntegrationConfigDto(
    string TenantId,
    string ClientId,
    bool HasClientSecret,
    string GraphBaseUrl,
    string ResourceManagerBaseUrl,
    int PageSize,
    IReadOnlyList<string> AzureSubscriptionIds);

public record PrinterTelemetryIntegrationConfigDto(
    bool HasCollectorApiKey);

public record IntegrationSettingsDto(
    NinjaIntegrationConfigDto Ninja,
    MicrosoftGraphIntegrationConfigDto MicrosoftGraph,
    PrinterTelemetryIntegrationConfigDto PrinterTelemetry);

public record UpdatePrinterTelemetryIntegrationConfigRequest(
    string? CollectorApiKey);

public record UpdateNinjaIntegrationConfigRequest(
    string BaseUrl,
    string ClientId,
    string? ClientSecret,
    string Scope,
    string TokenPath,
    string DevicesPath,
    int PageSize);

public record UpdateMicrosoftGraphIntegrationConfigRequest(
    string TenantId,
    string ClientId,
    string? ClientSecret,
    string GraphBaseUrl,
    string ResourceManagerBaseUrl,
    int PageSize,
    IReadOnlyList<string>? AzureSubscriptionIds);

public record IntegrationSyncSourceStatusDto(
    string Source,
    string Status,
    int SeenCount,
    int MatchedCount,
    int ExistingMatchedCount,
    int CreatedCount,
    int UnmatchedCount,
    string? Message);

public record IntegrationSyncStatusDto(
    string Target,
    bool IsRunning,
    string LastStatus,
    DateTime? LastRunStartedAtUtc,
    DateTime? LastRunCompletedAtUtc,
    DateTime? LastSuccessAtUtc,
    int LastSeenCount,
    int LastMatchedCount,
    string? LastMessage,
    string? LastTriggeredBy,
    IReadOnlyList<IntegrationSyncSourceStatusDto> Sources);

public record IntegrationSyncStatusSnapshotDto(
    IntegrationSyncStatusDto Ninja,
    IntegrationSyncStatusDto Microsoft);

public record TriggerIntegrationSyncResponseDto(
    string Target,
    bool Success,
    string Message,
    int SeenCount,
    int MatchedCount,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc);


public record PrinterIdentitySnapshotDto(
    string? Name,
    string? Hostname,
    string? IpAddress,
    string? Manufacturer,
    string? Model,
    string? SerialNumber);

public record PrinterStatusSnapshotDto(
    string? State,
    string? Alert);

public record PrinterUsageSnapshotDto(
    long? TotalPages,
    long? MonoPages,
    long? ColorPages);

public record PrinterConsumableSnapshotDto(
    string Name,
    decimal? PercentRemaining,
    string? Status);

public record IngestPrinterTelemetryRequest(
    string? CollectorId,
    DateTime? CapturedAtUtc,
    PrinterIdentitySnapshotDto Printer,
    PrinterStatusSnapshotDto? Status,
    PrinterUsageSnapshotDto? Usage,
    IReadOnlyList<PrinterConsumableSnapshotDto>? Consumables);

public record PrinterConsumableDto(
    string Name,
    decimal? PercentRemaining,
    string? Status);

public record PrinterTelemetryDto(
    int Id,
    string? CollectorId,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string Status,
    string? CurrentAlert,
    long? TotalPages,
    long? MonoPages,
    long? ColorPages,
    string ConsumableSummary,
    IReadOnlyList<PrinterConsumableDto> Consumables,
    DateTime? LastCapturedAtUtc,
    DateTime LastIngestedAtUtc);
