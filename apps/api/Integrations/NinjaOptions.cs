namespace api.Integrations;

public sealed class NinjaOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string TokenPath { get; set; } = "/ws/oauth/token";
    public string DevicesPath { get; set; } = "/v2/devices";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Scope { get; set; } = "monitoring";

    public int PageSize { get; set; } = 200;
}
