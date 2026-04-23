using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.DataModel.Models
{
    // Index for joins from NotebookConversation -> Notebook -> Project
    [Index(nameof(ProjectId))]
    public class Notebook
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [StringLength(255)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Slug { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Description { get; set; }

        [Required]
        public Guid ProjectId { get; set; }
        public Project Project { get; set; } = null!;

        // Legacy: Keep for backwards compatibility during migration
        public Guid? NotebookTemplateId { get; set; }
        
        // New: Direct reference to guide in Assistants table
        public Guid? GuideId { get; set; }
        public Assistant? Guide { get; set; }

        // Previously one-to-one; now a notebook can have many conversations
        public ICollection<NotebookConversation> Conversations { get; set; } = [];

        public ICollection<NotebookLink> NotebookLinks { get; set; } = [];
        public ICollection<NotebookSemiStructuredData> NotebookSemiStructuredDatas { get; set; } = [];
        public ICollection<NotebookFile> NotebookFiles { get; set; } = [];

        // Home Page Navigation Properties
        public Guid? HomePageFileId { get; set; }
        public NotebookFile? HomePageFile { get; set; }

        public Guid? HomePageConversationId { get; set; }
        public NotebookConversation? HomePageConversation { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        // Traceability: if this notebook was created by copying another notebook or message
        public Guid? SourceNotebookId { get; set; }
        public Notebook? SourceNotebook { get; set; }

        public Guid? SourceConversationMessageId { get; set; }
        public NotebookConversationMessage? SourceConversationMessage { get; set; }
    }
} 
