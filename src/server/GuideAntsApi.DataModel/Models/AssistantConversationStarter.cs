using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// A single conversation starter prompt for an assistant/guide.
    /// Sourced from HostExtensions/UI/conversationStarters.json.
    /// </summary>
    public class AssistantConversationStarter
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid AssistantId { get; set; }
        public Assistant Assistant { get; set; } = null!;

        /// <summary>
        /// The prompt text displayed to users.
        /// </summary>
        [Required]
        [StringLength(500)]
        public string Prompt { get; set; } = string.Empty;

        /// <summary>
        /// Display order (0-based).
        /// </summary>
        [Required]
        public int OrderIndex { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}

