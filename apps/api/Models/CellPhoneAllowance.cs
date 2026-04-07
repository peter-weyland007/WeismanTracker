using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class CellPhoneAllowance
{
    public int Id { get; set; }

    public int TrackedPersonId { get; set; }
    public TrackedPerson? TrackedPerson { get; set; }

    [MaxLength(50)]
    public string MobilePhoneNumber { get; set; } = string.Empty;

    public bool AllowanceGranted { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DeletedAtUtc { get; set; }
}
