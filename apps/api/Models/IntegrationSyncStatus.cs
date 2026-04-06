using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class IntegrationSyncStatus
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string SyncTarget { get; set; } = string.Empty;

    public bool IsRunning { get; set; }

    [MaxLength(32)]
    public string LastStatus { get; set; } = "Never";

    public DateTime? LastRunStartedAtUtc { get; set; }
    public DateTime? LastRunCompletedAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }

    public int LastSeenCount { get; set; }
    public int LastMatchedCount { get; set; }

    [MaxLength(2048)]
    public string? LastMessage { get; set; }

    [MaxLength(64)]
    public string? LastTriggeredBy { get; set; }

    public string? LastDetailsJson { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
