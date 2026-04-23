using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Represents a single text chunk with its vector embedding used for hybrid search.
/// </summary>
public class DocumentChunk
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// Logical index name (ProjectId as string or assistant vector store id).
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string IndexName { get; set; } = string.Empty;

    // Optional foreign keys – only one should be populated for project/notebook/assistant chunks.
    public Guid? ContentFileId { get; set; }
    public ContentFile? ContentFile { get; set; }

    public Guid? NotebookFileId { get; set; }
    public NotebookFile? NotebookFile { get; set; }

    public Guid? AssistantFileId { get; set; }
    public AssistantFile? AssistantFile { get; set; }

    // Denormalised filters (null for assistant chunks)
    public Guid? ProjectId { get; set; }
    public Guid? NotebookId { get; set; }

    /// <summary>
    /// Document ID for grouping chunks from the same document
    /// </summary>
    [MaxLength(255)]
    public string? DocumentId { get; set; }

    /// <summary>
    /// Position of this chunk within the source file (0-based).
    /// </summary>
    public int ChunkIndex { get; set; }

    [Required]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Vector embedding. EF Core 9 natively maps float[] to SQL Server vector(1536).
    /// </summary>
    public float[] Embedding { get; set; } = Array.Empty<float>();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
