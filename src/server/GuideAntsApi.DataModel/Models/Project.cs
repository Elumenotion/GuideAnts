using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class Project
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
        public string Description { get; set; } = string.Empty;

        public bool Deleted { get; set; }

        public Guid? HomePageContentFileId { get; set; }
        public ContentFile? HomePageContentFile { get; set; }
        public ICollection<Notebook> Notebooks { get; set; } = [];
        public ICollection<ContentFile> ContentFiles { get; set; } = [];
        public ICollection<ProjectFolder> Folders { get; set; } = [];
        public ICollection<Link> Links { get; set; } = [];
        public ICollection<SemiStructuredProjectData> SemiStructuredDatas { get; set; } = [];
        public ICollection<ProjectExternalAuth> ExternalAuths { get; set; } = [];

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
} 
