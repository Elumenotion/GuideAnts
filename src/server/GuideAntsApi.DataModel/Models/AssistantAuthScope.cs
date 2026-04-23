using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// A single scope string for an auth provider attached to an assistant/guide.
    /// </summary>
    public class AssistantAuthScope
    {
        [Required]
        public Guid AssistantAuthProviderId { get; set; }
        public AssistantAuthProvider AssistantAuthProvider { get; set; } = null!;

        [Key]
        [StringLength(200)]
        public string Scope { get; set; } = string.Empty;

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}


