using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models;

[Index(nameof(ScheduledJobId), nameof(StartedUtc))]
public class ProjectScheduledJobRun
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ScheduledJobId { get; set; }

    public ProjectScheduledJob ScheduledJob { get; set; } = null!;

    [Required]
    public ProjectScheduledJobTrigger TriggeredBy { get; set; }

    [Required]
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    [Required]
    public ProjectScheduledJobRunStatus Status { get; set; } = ProjectScheduledJobRunStatus.Running;

    [StringLength(4000)]
    public string? ErrorMessage { get; set; }

    public string? StandardOutput { get; set; }

    public string? StandardError { get; set; }

    public Guid? CreatedConversationId { get; set; }

    public int? ExitCode { get; set; }
}
