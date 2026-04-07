using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class TrackedPerson
{
    public int Id { get; set; }

    [MaxLength(120)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string? EmployeeNumber { get; set; }

    [MaxLength(50)]
    public string? MobilePhone { get; set; }

    [MaxLength(50)]
    public string? BusinessPhone { get; set; }

    public bool IsServiceAccount { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DeletedAtUtc { get; set; }

    public ICollection<TrackedComputer> Computers { get; set; } = new List<TrackedComputer>();
    public ICollection<CellPhoneAllowance> CellPhoneAllowances { get; set; } = new List<CellPhoneAllowance>();
}
