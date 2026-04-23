using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class ContentFileMarkdownShadow
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        [Required]
        public Guid OriginalContentFileVersionId { get; set; }
        
        [ForeignKey(nameof(OriginalContentFileVersionId))]
        public ContentFileVersion OriginalVersion { get; set; } = null!;

        [Required]
        [StringLength(64)] // SHA-256 hash length
        public string ContentHash { get; set; } = string.Empty;

        [Required]
        public string StoragePath { get; set; } = string.Empty;

        [Required]
        public long FileSize { get; set; }

        [Required]
        public MarkdownExtractionStatus Status { get; set; } = MarkdownExtractionStatus.Pending;

        public string? ErrorMessage { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? ProcessedAt { get; set; }

        /// <summary>
        /// Indicates whether this shadow file has been indexed in Kernel Memory.
        /// </summary>
        [Required]
        public bool IsIndexed { get; set; } = false;
    }

    public enum MarkdownExtractionStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Skipped // For unsupported types or files that don't need processing
    }
} 