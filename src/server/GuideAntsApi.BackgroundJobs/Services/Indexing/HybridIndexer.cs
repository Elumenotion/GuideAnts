using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;

namespace GuideAntsApi.BackgroundJobs.Services.Indexing;

internal sealed class HybridIndexer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEmbeddingService embeddings,
    ILogger<HybridIndexer> log) : IHybridIndexer
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory = dbFactory;
    private readonly IEmbeddingService _emb = embeddings;
    private readonly ILogger<HybridIndexer> _log = log;

    private const int MaxChunkBytes = 2048;

    public async Task IndexContentFileAsync(Guid contentFileVersionId, Guid projectId, string filePath, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(filePath, ct);
        text = SanitizeForEmbedding(text, $"content-file-version:{contentFileVersionId}");
        var chunks = ChunkText(text);
        var embeddings = await _emb.GetEmbeddingsAsync(chunks, EmbeddingPurpose.Document, ct);

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        
        // Get the ContentFile.Id from the version
        var contentFileId = await ctx.ContentFileVersions
            .Where(v => v.Id == contentFileVersionId)
            .Select(v => v.ContentFileId)
            .FirstOrDefaultAsync(ct);
            
        if (contentFileId == default)
        {
            _log.LogWarning("ContentFileVersion {Id} not found", contentFileVersionId);
            return;
        }
        
        // Delete existing chunks for this content file
        await ctx.DocumentChunks
            .Where(c => c.ContentFileId == contentFileId)
            .ExecuteDeleteAsync(ct);

        // Insert new chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            ctx.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = projectId.ToString(),
                ProjectId = projectId,
                ContentFileId = contentFileId,
                ChunkIndex = i,
                Content = chunks[i],
                Embedding = embeddings[i],
                CreatedUtc = DateTime.UtcNow
            });
        }

        await ctx.SaveChangesAsync(ct);
        _log.LogInformation("Indexed {ChunkCount} chunks for ContentFile {ContentFileId} from version {VersionId}", chunks.Count, contentFileId, contentFileVersionId);
    }

    public async Task IndexNotebookFileAsync(Guid notebookFileId, Guid notebookId, Guid projectId, string filePath, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(filePath, ct);
        text = SanitizeForEmbedding(text, $"notebook-file:{notebookFileId}");
        var chunks = ChunkText(text);
        var embeddings = await _emb.GetEmbeddingsAsync(chunks, EmbeddingPurpose.Document, ct);

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        
        // Delete existing chunks for this notebook file
        await ctx.DocumentChunks
            .Where(c => c.NotebookFileId == notebookFileId)
            .ExecuteDeleteAsync(ct);

        // Insert new chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            ctx.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = projectId.ToString(),
                ProjectId = projectId,
                NotebookId = notebookId,
                NotebookFileId = notebookFileId,
                ChunkIndex = i,
                Content = chunks[i],
                Embedding = embeddings[i],
                CreatedUtc = DateTime.UtcNow
            });
        }

        await ctx.SaveChangesAsync(ct);
        _log.LogInformation("Indexed {ChunkCount} chunks for NotebookFile {Id}", chunks.Count, notebookFileId);
    }

    public async Task IndexAssistantFolderAsync(string storeId, string folderPath, CancellationToken ct)
    {
        var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith(".km_"))
            .ToArray();

        if (files.Length == 0)
        {
            _log.LogDebug("No files found in assistant folder {FolderPath}", folderPath);
            return;
        }

        // Combine all file contents
        var allText = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                // Add filename as context
                allText.Add($"=== {Path.GetFileName(file)} ===\n{content}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to read assistant file {File}", file);
            }
        }

        if (allText.Count == 0) return;

        var combinedText = string.Join("\n\n", allText);
        combinedText = SanitizeForEmbedding(combinedText, $"assistant-folder:{storeId}");
        var chunks = ChunkText(combinedText);
        var embeddings = await _emb.GetEmbeddingsAsync(chunks, EmbeddingPurpose.Document, ct);

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);
        
        // Delete existing chunks for this assistant store
        await ctx.DocumentChunks
            .Where(c => c.IndexName == storeId && c.ProjectId == null)
            .ExecuteDeleteAsync(ct);

        // Insert new chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            ctx.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = storeId,
                ChunkIndex = i,
                Content = chunks[i],
                Embedding = embeddings[i],
                CreatedUtc = DateTime.UtcNow
            });
        }

        await ctx.SaveChangesAsync(ct);
        _log.LogInformation("Indexed {ChunkCount} chunks for assistant store {StoreId} from {FileCount} files", 
            chunks.Count, storeId, files.Length);
    }

    /// <summary>
    /// Index an assistant file's markdown content with AssistantId as the IndexName
    /// </summary>
    public async Task IndexAssistantFileAsync(
        Guid fileId,
        Guid assistantId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var indexName = assistantId.ToString(); // Use AssistantId as IndexName
        var documentId = fileId.ToString();

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var content = await new StreamReader(stream).ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(content))
        {
            _log.LogWarning("No content to index for AssistantFile {FileId}", fileId);
            return;
        }

        content = SanitizeForEmbedding(content, $"assistant-file:{fileId}");
        var chunks = ChunkText(content);
        if (chunks.Count == 0)
        {
            _log.LogWarning("No chunks generated for AssistantFile {FileId}", fileId);
            return;
        }

        var embeddings = await _emb.GetEmbeddingsAsync(chunks, EmbeddingPurpose.Document, cancellationToken);

        await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Delete existing chunks for this file
        await ctx.DocumentChunks
            .Where(c => c.IndexName == indexName && c.AssistantFileId == fileId)
            .ExecuteDeleteAsync(cancellationToken);

        // Insert new chunks
        for (int i = 0; i < chunks.Count; i++)
        {
            ctx.DocumentChunks.Add(new DocumentChunk
            {
                IndexName = indexName,
                DocumentId = documentId,
                AssistantFileId = fileId,
                ChunkIndex = i,
                Content = chunks[i],
                Embedding = embeddings[i],
                CreatedUtc = DateTime.UtcNow
            });
        }

        await ctx.SaveChangesAsync(cancellationToken);
        _log.LogInformation("Indexed {ChunkCount} chunks for AssistantFile {FileId} in index {IndexName}", 
            chunks.Count, fileId, indexName);
    }

    private string SanitizeForEmbedding(string text, string source)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var (sanitized, replacements) = MarkdownInlineImageSanitizer.ReplaceInlineImageDataUrls(text);

        if (replacements > 0)
        {
            _log.LogDebug(
                "Sanitized embedding input for {Source}: replaced {ReplacementCount} inline image payloads (chars {OriginalLength} -> {SanitizedLength})",
                source,
                replacements,
                text.Length,
                sanitized.Length);
        }

        return sanitized;
    }

    private static List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        // Byte-based chunking with 50% overlap
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        int start = 0;

        while (start < bytes.Length)
        {
            int chunkSize = Math.Min(MaxChunkBytes, bytes.Length - start);
            var chunkBytes = new byte[chunkSize];
            Array.Copy(bytes, start, chunkBytes, 0, chunkSize);
            
            var chunkText = System.Text.Encoding.UTF8.GetString(chunkBytes);
            chunks.Add(chunkText);

            // Move start position with 50% overlap
            int stepSize = Math.Max(1, chunkSize / 2);
            start += stepSize;
            
            // Break if remaining text is less than half a chunk
            if (start >= bytes.Length - stepSize) break;
        }

        return chunks;
    }

}
