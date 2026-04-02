namespace api.Models;

public class CatEtActivationEvent
{
    public int Id { get; set; }
    public int CatEtLicenseId { get; set; }
    public CatEtLicense CatEtLicense { get; set; } = null!;
    public string EventType { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
