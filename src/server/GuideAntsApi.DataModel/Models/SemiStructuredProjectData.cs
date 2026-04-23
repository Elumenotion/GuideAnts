using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class SemiStructuredProjectData
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public string Placeholder { get; set; } = string.Empty;

        [Required]
        public Guid ProjectId { get; set; }
        public Project Project { get; set; } = null!;

        public ICollection<NotebookSemiStructuredData> NotebookSemiStructuredDatas { get; set; } = [];

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
} 