using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class TrackedComputer
{
    public int Id { get; set; }

    [MaxLength(120)]
    public string Hostname { get; set; } = string.Empty;

    [MaxLength(120)]
    public string AssetTag { get; set; } = string.Empty;

    public int? TrackedPersonId { get; set; }

    public TrackedPerson? TrackedPerson { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool ExcludeFromSync { get; set; }

    public bool HiddenFromTable { get; set; }

    public bool IsMobileDevice { get; set; }

    [MaxLength(40)]
    public string AssetCategory { get; set; } = "Computer";

    public DateTime? DeletedAtUtc { get; set; }

    public ICollection<CatEtLicense> CatEtLicenses { get; set; } = new List<CatEtLicense>();
}
