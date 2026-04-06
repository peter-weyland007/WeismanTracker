using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class IntegrationProviderConfig
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Provider { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? BaseUrl { get; set; }

    [MaxLength(256)]
    public string? TenantId { get; set; }

    [MaxLength(256)]
    public string? ClientId { get; set; }

    [MaxLength(512)]
    public string? ClientSecret { get; set; }

    [MaxLength(128)]
    public string? Scope { get; set; }

    [MaxLength(128)]
    public string? TokenPath { get; set; }

    [MaxLength(128)]
    public string? DevicesPath { get; set; }

    public int? PageSize { get; set; }

    [MaxLength(256)]
    public string? ResourceManagerBaseUrl { get; set; }

    public string? AzureSubscriptionIdsCsv { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
