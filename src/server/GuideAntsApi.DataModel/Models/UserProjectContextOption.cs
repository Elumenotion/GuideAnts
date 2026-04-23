using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// A single key–value context option for a specific user within a project.
    /// </summary>
    public class UserProjectContextOption
    {
        // Composite primary key
        public Guid UserId { get; set; }
        public Guid ProjectId { get; set; }

        [MaxLength(120)]
        public string Key { get; set; } = null!;

        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Creation timestamp (configured globally in ApplicationDbContext).
        /// </summary>
        public DateTime Created { get; set; }

        // Navigation properties
        public User User { get; set; } = null!;
        public Project Project { get; set; } = null!;
    }
}


