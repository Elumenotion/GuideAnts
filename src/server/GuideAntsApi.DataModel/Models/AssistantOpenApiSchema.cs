using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// An OpenAPI 3.0 schema defining external API operations for an assistant.
    /// Links to AssistantAuthProvider via ApiHost for authentication.
    /// </summary>
    public class AssistantOpenApiSchema
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid AssistantId { get; set; }
        public Assistant Assistant { get; set; } = null!;

        /// <summary>
        /// Schema name/identifier (e.g., "calendar", "tasks", "weather").
        /// Used for display and referencing the schema.
        /// </summary>
        [Required]
        [StringLength(128)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// API host extracted from servers[0].url (e.g., "graph.microsoft.com", "api.search.brave.com").
        /// Used to match with AuthProvider.ProviderId.
        /// </summary>
        [Required]
        [StringLength(255)]
        public string ApiHost { get; set; } = string.Empty;

        /// <summary>
        /// The complete OpenAPI 3.0 specification as JSON string.
        /// Editable in UI, validated before save.
        /// </summary>
        [Required]
        public string SpecificationJson { get; set; } = string.Empty;

        /// <summary>
        /// Optional authentication provider for this API.
        /// Auth configuration belongs to the schema that needs it.
        /// </summary>
        public Guid? AuthProviderId { get; set; }
        public AssistantAuthProvider? AuthProvider { get; set; }

        /// <summary>
        /// Individual operations parsed from this OpenAPI schema.
        /// Each operation represents a single path+method combination.
        /// </summary>
        public ICollection<AssistantOpenApiOperation> Operations { get; set; } = new List<AssistantOpenApiOperation>();

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }
    }
}

