using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    public class NotebookLink
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid NotebookId { get; set; }
        public Notebook Notebook { get; set; } = null!;

        [Required]
        public Guid LinkId { get; set; }
        public Link Link { get; set; } = null!;

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
} 