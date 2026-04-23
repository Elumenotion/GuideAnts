using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Represents a single operation (path+method) parsed from an OpenAPI schema.
    /// Stores the tool definition and schema fragment for individual editing.
    /// </summary>
    public class AssistantOpenApiOperation
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid SchemaId { get; set; }
        public AssistantOpenApiSchema Schema { get; set; } = null!;

        /// <summary>
        /// The operationId from the OpenAPI spec, or generated as "{method}_{path}".
        /// Must be unique within the schema.
        /// </summary>
        [Required]
        [StringLength(255)]
        public string OperationId { get; set; } = string.Empty;

        /// <summary>
        /// HTTP method: GET, POST, PUT, DELETE, PATCH, etc.
        /// </summary>
        [Required]
        [StringLength(10)]
        public string Method { get; set; } = string.Empty;

        /// <summary>
        /// API path template from OpenAPI spec (e.g., "/v1.0/me/onenote/notebooks").
        /// </summary>
        [Required]
        [StringLength(1000)]
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Operation summary/description from OpenAPI spec.
        /// </summary>
        [StringLength(2000)]
        public string? Summary { get; set; }

        /// <summary>
        /// The complete OpenAI tool definition JSON for this operation.
        /// This is what gets sent to the LLM.
        /// </summary>
        [Required]
        public string ToolDefinitionJson { get; set; } = string.Empty;

        /// <summary>
        /// The OpenAPI schema fragment for this specific operation.
        /// Contains the path item and method details for editing.
        /// Structure: { "path": "/...", "method": "get", "operation": { ... } }
        /// </summary>
        [Required]
        public string SchemaFragmentJson { get; set; } = string.Empty;

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }
    }
}

