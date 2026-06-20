using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Project-bounded script execution environment configuration for a guide or assistant.
/// The same guide/assistant asset can have different values in different projects.
/// </summary>
public class ProjectAssistantEnvironment
{
    [Required]
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required]
    public Guid AssistantId { get; set; }
    public Assistant Assistant { get; set; } = null!;

    public string? EnvironmentConfigJson { get; set; }

    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? Updated { get; set; }
}
