using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// A binary file resource attached to an assistant/guide.
    /// Stores CodeInterpreter files, VectorStore documents, and other binary assets.
    /// NOTE: OpenAPI schemas are NOT stored here - see AssistantOpenApiSchema entity.
    /// </summary>
    public class AssistantFile
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid AssistantId { get; set; }
        public Assistant Assistant { get; set; } = null!;

        /// <summary>
        /// Logical folder kind to mirror disk locations.
        /// Valid values: CodeInterpreter | VectorStore | HostExtensions
        /// NOTE: "OpenAPI" is NO LONGER valid - OpenAPI schemas use AssistantOpenApiSchema entity.
        /// </summary>
        [Required]
        [StringLength(32)]
        public string FolderKind { get; set; } = string.Empty; // CodeInterpreter | VectorStore | HostExtensions

        /// <summary>
        /// For VectorStore files, stores the vector store identifier from path.
        /// </summary>
        [StringLength(128)]
        public string? VectorStoreName { get; set; }

        /// <summary>
        /// Relative path within the assistant scope (e.g., "OpenAPI/calendar.json").
        /// </summary>
        [Required]
        [StringLength(1024)]
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>
        /// Optional file content for small resources; large binaries may live elsewhere.
        /// </summary>
        public byte[]? ContentBytes { get; set; }

        /// <summary>
        /// Optional content type/metadata.
        /// </summary>
        [StringLength(128)]
        public string? ContentType { get; set; }

        [Required]
        public DateTime Created { get; set; } = DateTime.UtcNow;

        public DateTime? Updated { get; set; }
    }
}


