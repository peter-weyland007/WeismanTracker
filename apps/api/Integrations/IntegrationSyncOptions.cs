namespace api.Integrations;

public sealed class IntegrationSyncOptions
{
    public int UserSyncMinutes { get; set; } = 60;
    public int ComputerSyncMinutes { get; set; } = 30;
}
