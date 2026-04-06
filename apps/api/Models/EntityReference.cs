using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class EntityReference
{
    public int Id { get; set; }

    public ResourceEntityType EntityType { get; set; }

    public int EntityId { get; set; }

    public int ResourceDefinitionId { get; set; }

    [MaxLength(256)]
    public string ExternalId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? ExternalKey { get; set; }

    public ReferenceSyncStatus SyncStatus { get; set; } = ReferenceSyncStatus.Linked;

    public DateTime LastSyncedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? FirstLinkedAtUtc { get; set; }

    public DateTime? LastSeenAtUtc { get; set; }

    public string? MetadataJson { get; set; }

    public ResourceDefinition ResourceDefinition { get; set; } = null!;
}
