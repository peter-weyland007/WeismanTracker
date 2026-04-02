namespace web.Models;

public record TrackedPersonDto(int Id, string FullName, string? Email, DateTime CreatedAtUtc);
public record CreateTrackedPersonRequest(string FullName, string? Email);

public record TrackedComputerDto(int Id, string Hostname, string AssetTag, int? TrackedPersonId, string? TrackedPersonName, DateTime CreatedAtUtc);
public record CreateTrackedComputerRequest(string Hostname, string AssetTag, int? TrackedPersonId);

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