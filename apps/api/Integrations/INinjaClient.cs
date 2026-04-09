namespace api.Integrations;

public interface INinjaClient
{
    Task<IReadOnlyList<NinjaDeviceDto>> GetDevicesAsync(CancellationToken cancellationToken);
}

public sealed record NinjaDeviceDto(
    string ExternalId,
    string? DeviceName,
    string? SerialNumber,
    string? Hostname,
    string? Os,
    string? Manufacturer,
    string? Model,
    string? DeviceType,
    string? ChassisType,
    DateTime? LastSeenAtUtc);
