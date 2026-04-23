using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Join table linking assistants/guides to their assigned tools.
    /// </summary>
    public class AssistantTool
    {
        [Required]
        public Guid AssistantId { get; set; }
        public Assistant Assistant { get; set; } = null!;

        [Required]
        public Guid ToolId { get; set; }
        public Tool Tool { get; set; } = null!;

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}

