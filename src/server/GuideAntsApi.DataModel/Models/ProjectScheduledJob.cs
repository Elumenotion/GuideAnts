using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

[Index(nameof(ProjectId), nameof(IsEnabled), nameof(NextRunUtc))]
[Index(nameof(ProjectId), nameof(Name), IsUnique = true)]
public class ProjectScheduledJob
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    [Required]
    [StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public ProjectScheduledJobType JobType { get; set; }

    [Required]
    public Guid NotebookId { get; set; }

    public Notebook Notebook { get; set; } = null!;

    [Required]
    public bool IsEnabled { get; set; } = true;

    [Required]
    [StringLength(128)]
    public string CronExpression { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string TimeZoneId { get; set; } = "UTC";

    [StringLength(512)]
    public string? ConversationTitle { get; set; }

    public string? Prompt { get; set; }

    [StringLength(255)]
    public string? AssistantName { get; set; }

    public Guid? ScriptNotebookFileId { get; set; }

    public NotebookFile? ScriptNotebookFile { get; set; }

    [Required]
    public Guid CreatedByUserId { get; set; }

    public User CreatedByUser { get; set; } = null!;

    [Required]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Required]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? NextRunUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public ProjectScheduledJobLastRunStatus? LastRunStatus { get; set; }

    [Required]
    public bool ExposeSandboxWireApi { get; set; }

    public Guid? WireTargetAssistantId { get; set; }

    [StringLength(512)]
    public string? WireAttributionConversationTitle { get; set; }

    [Required]
    public bool WireCreateAttributionConversationPerRun { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? WireDailyLimitUsd { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? WireMonthlyLimitUsd { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = null!;

    public ICollection<ProjectScheduledJobRun> Runs { get; set; } = [];
}
