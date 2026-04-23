using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// A single key-value context option for a specific project.
    /// </summary>
    public class ProjectContextOption
    {
        // Composite primary key
        public Guid ProjectId { get; set; }

        [MaxLength(120)]
        public string Key { get; set; } = null!;

        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Creation timestamp (configured globally in ApplicationDbContext).
        /// </summary>
        public DateTime Created { get; set; }

        // Navigation properties
        public Project Project { get; set; } = null!;
    }
}


