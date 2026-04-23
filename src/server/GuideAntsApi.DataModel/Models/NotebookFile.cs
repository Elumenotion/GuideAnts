using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Represents a physical file that exists inside a notebook's dedicated filesystem root.
/// The filesystem is considered the source of truth – this entity is updated via the
/// NotebookFileSyncService to mirror the current state on disk.
/// </summary>
public class NotebookFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Owning notebook. Deleting a notebook should delete its files.
    /// </summary>
    [Required]
    public Guid NotebookId { get; set; }
    public Notebook Notebook { get; set; } = null!;

    /// <summary>
    /// Path of the file relative to the notebook root (e.g. "data/plot.png").
    /// This combined with <see cref="NotebookId"/> must be unique.
    /// </summary>
    [Required]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Size of the file in bytes. Populated by the sync service.
    /// </summary>
    [Required]
    public long FileSize { get; set; }

    /// <summary>
    /// UTC last modified timestamp pulled from the filesystem.
    /// </summary>
    [Required]
    public DateTime LastModifiedUtc { get; set; }

    /// <summary>
    /// SHA-256 hash of the file contents for precise change detection.
    /// </summary>
    [Required]
    public string FileHash { get; set; } = string.Empty;

    /// <summary>
    /// If this file originated from a project file version, this links back to that version
    /// to preserve lineage. Otherwise, null indicates a notebook-native file.
    /// </summary>
    public Guid? OriginContentFileVersionId { get; set; }
    public ContentFileVersion? OriginContentFileVersion { get; set; }

    /// <summary>
    /// Record creation timestamp. Stored in UTC.
    /// </summary>
    [Required]
    public DateTime Created { get; set; } = DateTime.UtcNow;


    /// <summary>
    /// Stable identifier used by Kernel Memory for uploads.
    /// Generated from <c>NotebookId</c> + <c>RelativePath</c> and stored so it
    /// can be reused for subsequent uploads.
    /// </summary>
    [Required]
    [StringLength(255)]
    public string DocumentId { get; private set; } = string.Empty;

    /// <summary>
    /// Generates the <see cref="DocumentId"/> for this file. Must be called
    /// after <see cref="NotebookId"/> and <see cref="RelativePath"/> are set.
    /// </summary>
    /// <param name="notebookId">Owning notebook identifier (passed explicitly
    /// to avoid relying on navigation properties during construction).</param>
    public void GenerateDocumentId(Guid notebookId)
    {
        // Ensure we hash the *full* relative path, not just the file name, so that
        // files with the same name that live in different folders get distinct
        // document identifiers.
        var normalizedPath = RelativePath
            .Replace('\\', '/')        // normalise path separators
            .ToLowerInvariant();         // make case-insensitive across OSes
        
        var raw = $"{notebookId}:{normalizedPath}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        // URL-safe Base64 (RFC 4648 §5) without padding for compactness
        DocumentId = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
} 