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
        /// Optional JSON string containing provider runtime configuration.
        /// Llama-cpp: { routerModelId } only. Other providers: null or provider-specific JSON
        /// without runtimeProfileId. Chat behavior (sampling, reasoning, thinking) lives on
        /// this row's JSON columns — see docs/model-chat-behavior-contract.md.
        /// </summary>
        public string? RuntimeConfigJson { get; set; }

        /// <summary>
        /// When true, system and developer messages are merged into a single system message
        /// before sending to the model. Used by llama-cpp models at chat runtime.
        /// </summary>
        [Required]
        public bool CombineSystemAndDeveloperMessages { get; set; } = true;

        /// <summary>
        /// Optional regex pattern to identify and strip thinking blocks from model output.
        /// </summary>
        [StringLength(256)]
        public string? ThoughtBlockPattern { get; set; }

        /// <summary>
        /// JSON dictionary of sampling parameter definitions keyed by parameter name.
        /// Authority for guide/assistant parameter surfaces and request defaults (all providers).
        /// </summary>
        [Required]
        public string SamplingParametersJson { get; set; } = "{}";

        /// <summary>
        /// JSON object mapping reasoning choices to request actions.
        /// Used at inference for llama-cpp, hf-inference-chat, and openrouter-chat when configured.
        /// </summary>
        [Required]
        public string ThinkingControlJson { get; set; } = "{}";

        /// <summary>
        /// JSON object of concrete chat request fields merged when tools are present.
        /// </summary>
        [Required]
        public string RequestFieldsWhenToolsPresentJson { get; set; } = "{}";

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

        public LocalModelInstallation? Installation { get; set; }

    }
}

