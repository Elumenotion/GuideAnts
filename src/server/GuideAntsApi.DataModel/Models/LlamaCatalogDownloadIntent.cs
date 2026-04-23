using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Durable state for pending catalog auto-registration after a delegated
    /// llama runtime download operation completes.
    /// </summary>
    public class LlamaCatalogDownloadIntent
    {
        [Key]
        [Required]
        [StringLength(128)]
        public string OperationId { get; set; } = string.Empty;

        [Required]
        [StringLength(128)]
        public string ModelId { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        [StringLength(64)]
        public string RuntimeProfileId { get; set; } = string.Empty;

        [Required]
        [StringLength(255)]
        public string RouterModelId { get; set; } = string.Empty;

        [StringLength(1024)]
        public string? Description { get; set; }

        [Required]
        [StringLength(128)]
        public string ResourceGroupKey { get; set; } = string.Empty;

        [Required]
        public bool IsActive { get; set; }

        public int? DisplayOrder { get; set; }

        public string? LoadParamsJson { get; set; }

        /// <summary>
        /// Optional per-alias prompt context (tokens) written to router-models.ini as <c>ctx-size</c> for llama-server.
        /// </summary>
        public int? RouterContextSize { get; set; }

        /// <summary>
        /// Optional per-alias prompt cache budget (MiB) written as <c>cache-ram</c> in router-models.ini.
        /// </summary>
        public int? RouterCacheRamMib { get; set; }

        [Required]
        public bool ParallelToolCalls { get; set; }

        [Required]
        public bool RegisteringCatalogObserved { get; set; }

        [StringLength(128)]
        public string? LastErrorCode { get; set; }

        [StringLength(64)]
        public string? LastErrorStep { get; set; }

        [StringLength(2048)]
        public string? LastErrorMessage { get; set; }

        [StringLength(1024)]
        public string? LastErrorRemediation { get; set; }

        [Required]
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        [Required]
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
