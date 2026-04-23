using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Represents a globally available tool that can be assigned to assistants.
    /// Provides a catalog of selectable tools with metadata.
    /// </summary>
    public class Tool
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Unique tool identifier/type (e.g., "file_search", "code_interpreter", "set_user_context_options").
        /// </summary>
        [Required]
        [StringLength(128)]
        public string ToolType { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable display name.
        /// </summary>
        [Required]
        [StringLength(255)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of what this tool does.
        /// </summary>
        [StringLength(1024)]
        public string? Description { get; set; }

        /// <summary>
        /// Category for grouping tools in the UI (e.g., "Search", "Code", "Integration").
        /// </summary>
        [StringLength(100)]
        public string? Category { get; set; }

        /// <summary>
        /// Whether this tool is currently available for selection.
        /// </summary>
        [Required]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Optional ordering hint for UI display within category.
        /// </summary>
        public int? DisplayOrder { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }

        public ICollection<AssistantTool> AssistantTools { get; set; } = [];
    }
}

