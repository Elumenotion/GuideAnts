using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Join table for a guide's crew of assistants.
    /// Links a Guide (Assistant with Kind=Guide) to the Assistants in its crew.
    /// </summary>
    public class GuideMember
    {
        [Required]
        public Guid GuideId { get; set; }
        public Assistant Guide { get; set; } = null!;

        [Required]
        public Guid AssistantId { get; set; }
        public Assistant Assistant { get; set; } = null!;

        /// <summary>
        /// Optional display order within the crew.
        /// </summary>
        public int? DisplayOrder { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}

