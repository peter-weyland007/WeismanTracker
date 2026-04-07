namespace api.Integrations;

public interface IMicrosoftGraphClient
{
    Task<IReadOnlyList<GraphUserDto>> GetUsersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphDeviceDto>> GetDevicesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<IntuneManagedDeviceDto>> GetManagedDevicesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AzureVmDto>> GetVirtualMachinesAsync(CancellationToken cancellationToken);
}

public sealed record GraphUserDto(
    string ExternalId,
    string? UserPrincipalName,
    string? Mail,
    string? DisplayName,
    string? MobilePhone,
    string? BusinessPhone);

public sealed record GraphDeviceDto(
    string ExternalId,
    string? DisplayName,
    string? DeviceId,
    string? SerialNumber);

public sealed record IntuneManagedDeviceDto(
    string ExternalId,
    string? DeviceName,
    string? AzureAdDeviceId,
    string? SerialNumber);

public sealed record AzureVmDto(
    string ExternalId,
    string? Name,
    string? VmId);
