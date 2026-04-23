using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Represents a language model available for use in assistants and guides.
    /// Provides a catalog of selectable models with metadata.
    /// </summary>
    public class Model
    {
        /// <summary>
        /// Model identifier (e.g., "gpt-4.1", "gpt-4o", "o1-preview").
        /// </summary>
        [Key]
        [Required]
        [StringLength(128)]
        public string ModelId { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable display name.
        /// </summary>
        [Required]
        [StringLength(255)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Provider identifier (e.g., "openai-chat", "openai-responses", "anthropic").
        /// </summary>
        [Required]
        [StringLength(32)]
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Optional description of model capabilities and use cases.
        /// </summary>
        [StringLength(1024)]
        public string? Description { get; set; }

        /// <summary>
        /// Optional JSON array string of valid reasoning choices for this model.
        /// Example: ["None","Enabled"].
        /// </summary>
        public string? ReasoningChoicesJson { get; set; }

        /// <summary>
        /// Optional JSON string containing local runtime configuration for local models (e.g., llama-cpp).
        /// Canonical shape for llama-cpp:
        /// { routerModelId, resourceGroupKey, runtimeProfileId, loadParams?, parallelToolCalls? }.
        /// </summary>
        public string? LocalRuntimeJson { get; set; }

        /// <summary>
        /// Whether this model is currently available for selection.
        /// </summary>
        [Required]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Optional ordering hint for UI display.
        /// </summary>
        public int? DisplayOrder { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }

        public ICollection<Assistant> Assistants { get; set; } = [];
    }
}

