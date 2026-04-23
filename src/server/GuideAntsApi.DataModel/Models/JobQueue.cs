using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models;

public enum JobStatus : byte
{
    Pending = 0,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    Abandoned = 5,
    Cancelled = 6
}

public sealed class JobQueue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    [Required]
    [MaxLength(64)]
    public string JobType { get; set; } = string.Empty; // e.g., ExtractContentVersionMarkdown
    
    [Required]
    public string PayloadJson { get; set; } = string.Empty; // serialized payload
    
    public int Priority { get; set; } = 0;
    
    [Required]
    public JobStatus Status { get; set; } = JobStatus.Pending;
    
    [Required]
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    
    public DateTime? LeaseUntil { get; set; }
    
    [Required]
    public Guid ClaimToken { get; set; } = Guid.Empty; // Empty = unclaimed, otherwise claimed
    
    public int Attempts { get; set; } = 0;
    
    public int MaxAttempts { get; set; } = 5;
    
    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow; // Follows existing convention
    
    [Required]
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    
    public string? ErrorMessage { get; set; }
    
    public Guid? CorrelationId { get; set; }
    
    [Timestamp] // Optimistic concurrency control
    public byte[] RowVersion { get; set; } = null!;
}

