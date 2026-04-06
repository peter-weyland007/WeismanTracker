using System.ComponentModel.DataAnnotations;

namespace api.Models;

public class ResourceDefinition
{
    public int Id { get; set; }

    public ResourceEntityType EntityType { get; set; }

    [MaxLength(120)]
    public string Provider { get; set; } = string.Empty;

    [MaxLength(120)]
    public string ResourceType { get; set; } = string.Empty;

    [MaxLength(180)]
    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public ICollection<EntityReference> EntityReferences { get; set; } = new List<EntityReference>();
}
