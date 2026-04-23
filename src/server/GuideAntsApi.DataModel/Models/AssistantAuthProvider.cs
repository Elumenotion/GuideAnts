using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Represents authentication configuration for an API host.
    /// Owned by AssistantOpenApiSchema - auth belongs to the schema that calls the API.
    /// </summary>
    public class AssistantAuthProvider
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Provider identifier, e.g., "graph.microsoft.com" or host for service_http.
        /// </summary>
        [Required]
        [StringLength(255)]
        public string ProviderId { get; set; } = string.Empty;

        /// <summary>
        /// Auth type string from manifest (e.g., oauth, service_http).
        /// </summary>
        [Required]
        [StringLength(32)]
        public string AuthType { get; set; } = string.Empty;

        /// <summary>
        /// Optional OAuth client id.
        /// </summary>
        [StringLength(200)]
        public string? ClientId { get; set; }

        /// <summary>
        /// Optional tenant string for OAuth (e.g., organizations).
        /// </summary>
        [StringLength(100)]
        public string? Tenant { get; set; }

        /// <summary>
        /// For service_http: header name to set.
        /// </summary>
        [StringLength(100)]
        public string? HeaderName { get; set; }

        /// <summary>
        /// Value template (e.g., "Bearer {access_token}" or "{env:SEARCH_API_KEY}").
        /// </summary>
        [StringLength(512)]
        public string? ValueTemplate { get; set; }

        /// <summary>
        /// Optional user config policy string from manifest (e.g., none).
        /// </summary>
        [StringLength(50)]
        public string? UserConfigPolicy { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public ICollection<AssistantAuthScope> Scopes { get; set; } = [];
    }
}


