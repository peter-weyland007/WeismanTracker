using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class PrinterTelemetryRecord
{
    public int Id { get; set; }

    [MaxLength(256)]
    public string IdentityKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? CollectorId { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? Hostname { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [MaxLength(64)]
    public string? Manufacturer { get; set; }

    [MaxLength(128)]
    public string? Model { get; set; }

    [MaxLength(128)]
    public string? SerialNumber { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "Unknown";

    [MaxLength(512)]
    public string? CurrentAlert { get; set; }

    public long? TotalPages { get; set; }
    public long? MonoPages { get; set; }
    public long? ColorPages { get; set; }

    [MaxLength(512)]
    public string? ConsumableSummary { get; set; }

    public string? ConsumablesJson { get; set; }

    public DateTime? LastCapturedAtUtc { get; set; }
    public DateTime LastIngestedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
