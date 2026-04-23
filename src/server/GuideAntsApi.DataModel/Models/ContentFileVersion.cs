using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class ContentFileVersion
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; }

        [Required]
        public Guid ContentFileId { get; set; }
        public ContentFile ContentFile { get; set; } = null!;

        [Required]
        public int VersionNumber { get; set; }

        [Required]
        [StringLength(255)]
        public string FileName { get; set; } = string.Empty;

        // NEW: Content-addressable storage fields
        [StringLength(64)] // SHA-256 hash length
        public string? ContentHash { get; set; }
        
        public string? StoragePath { get; set; }
        
        // IMMUTABLE: Path where version was originally created (never changes after creation)
        public string? OriginalRelativePath { get; set; }
        
        public Guid? OriginalFolderId { get; set; }
        public ProjectFolder? OriginalFolder { get; set; }
        
        // DEPRECATED: Will be removed after migration
        [Required]
        public string Path { get; set; } = string.Empty;

        [Required] 
        public string RelativePath { get; set; } = string.Empty;
        
        [Required]
        public long FileSize { get; set; }

        [Required]
        public string ContentType { get; set; } = string.Empty;

        [Required]
        public bool Indexed { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public Guid? OriginVersionId { get; set; }
        public ContentFileVersion? OriginVersion { get; set; }

        public bool FromNotebook { get; set; } = false;
        public Guid? OriginNotebookId { get; set; }
        public Notebook? OriginNotebook { get; set; }
        public Guid? OriginNotebookFileId { get; set; }
        public NotebookFile? OriginNotebookFile { get; set; }
    }
} 