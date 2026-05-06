using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Defines a model family's sampling parameters, thinking control actions,
    /// and message normalization behavior. Referenced by Model.LocalRuntimeJson
    /// via the runtimeProfileId field.
    /// </summary>
    public class RuntimeProfile
    {
        [Key]
        [Required]
        [StringLength(64)]
        public string ProfileId { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(1024)]
        public string? Description { get; set; }

        /// <summary>
        /// When true, system and developer messages are merged into a single system message
        /// before sending to the model.
        /// </summary>
        [Required]
        public bool CombineSystemAndDeveloperMessages { get; set; } = true;

        /// <summary>
        /// Optional regex pattern to identify and strip thinking blocks from model output.
        /// E.g. for Qwen: &lt;think&gt;[\s\S]*?&lt;/think&gt;
        /// </summary>
        [StringLength(256)]
        public string? ThoughtBlockPattern { get; set; }

        /// <summary>
        /// JSON dictionary of SamplingParameterDefinition keyed by parameter name.
        /// Each entry defines key, label, description, min, max, step, default, displayOrder, exposedInGuideBuilder.
        /// </summary>
        [Required]
        public string SamplingParametersJson { get; set; } = "{}";

        /// <summary>
        /// JSON object with { defaultChoice, choiceActions: { "choice": [{ target, key, value }] } }.
        /// Maps each reasoning choice to a list of actions to apply to the request.
        /// </summary>
        [Required]
        public string ThinkingControlJson { get; set; } = "{}";

        /// <summary>
        /// Discriminates between local (llama-cpp) and cloud profiles.
        /// Local profiles expose all fields; cloud profiles expose only
        /// sampling parameters.
        /// </summary>
        [Required]
        [StringLength(16)]
        public string Kind { get; set; } = "local";

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }
    }
}
