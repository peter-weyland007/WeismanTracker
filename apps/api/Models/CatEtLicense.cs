using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class CatEtLicense
{
    public int Id { get; set; }

    [MaxLength(128)]
    public string SerialNumber { get; set; } = string.Empty;

    [MaxLength(128)]
    public string LicenseKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ActivationId { get; set; } = string.Empty;

    public CatEtLicenseStatus Status { get; set; } = CatEtLicenseStatus.Available;

    public DateTime? ActivatedAtUtc { get; set; }

    public DateTime? LastResetAtUtc { get; set; }

    public int? TrackedComputerId { get; set; }

    public TrackedComputer? TrackedComputer { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DeletedAtUtc { get; set; }

    public ICollection<CatEtActivationEvent> ActivationEvents { get; set; } = new List<CatEtActivationEvent>();
}
