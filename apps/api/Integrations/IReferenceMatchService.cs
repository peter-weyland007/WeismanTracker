namespace api.Integrations;

public interface IReferenceMatchService
{
    Task<int?> MatchUserEntityIdAsync(GraphUserDto user, CancellationToken cancellationToken);
    Task<int?> MatchComputerEntityIdFromNinjaAsync(NinjaDeviceDto device, CancellationToken cancellationToken);
    Task<int?> MatchComputerEntityIdFromGraphDeviceAsync(GraphDeviceDto device, CancellationToken cancellationToken);
    Task<int?> MatchComputerEntityIdFromIntuneAsync(IntuneManagedDeviceDto device, CancellationToken cancellationToken);
    Task<int?> MatchComputerEntityIdFromAzureVmAsync(AzureVmDto vm, CancellationToken cancellationToken);
}
