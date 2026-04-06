namespace api.Integrations;

public sealed class MicrosoftGraphOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com";
    public int PageSize { get; set; } = 999;

    public string ResourceManagerBaseUrl { get; set; } = "https://management.azure.com";
    public List<string> AzureSubscriptionIds { get; set; } = [];
}
