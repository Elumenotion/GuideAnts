using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Flattens contextOptions.json (key/value pairs) for an assistant/guide.
    /// </summary>
    public class AssistantContextOption
    {
        [Required]
        public Guid AssistantId { get; set; }
        public Assistant Assistant { get; set; } = null!;

        [Required]
        [StringLength(128)]
        public string Key { get; set; } = string.Empty;

        [StringLength(1024)]
        public string? Value { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;
    }
}


