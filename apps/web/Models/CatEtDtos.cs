namespace web.Models;

public record TrackedPersonDto(int Id, string FullName, string? Email, DateTime CreatedAtUtc);
public record CreateTrackedPersonRequest(string FullName, string? Email);

public record TrackedComputerDto(
    int Id,
    string Hostname,
    string AssetTag,
    int? TrackedPersonId,
    string? TrackedPersonName,
    DateTime CreatedAtUtc,
    bool ExcludeFromSync,
    bool HiddenFromTable,
    bool IsMobileDevice,
    string AssetCategory);
public record CreateTrackedComputerRequest(string Hostname, string AssetTag, int? TrackedPersonId);
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
    string? AssetTag,
    string? TrackedPersonName);

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

public record IntegrationSettingsDto(
    NinjaIntegrationConfigDto Ninja,
    MicrosoftGraphIntegrationConfigDto MicrosoftGraph);

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