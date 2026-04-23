using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class ExtractAssistantFileMarkdownHandler : JobHandlerBase<ExtractAssistantFileMarkdownJob>
{
    private readonly IDocumentIntelligenceService _docIntel;
    private readonly IOptions<MarkdownExtractionOptions> _extractionOptions;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IJobQueueService _jobQueue;
    private readonly IConfiguration _configuration;

    public ExtractAssistantFileMarkdownHandler(
        ILogger<ExtractAssistantFileMarkdownHandler> logger,
        IDocumentIntelligenceService docIntel,
        IOptions<MarkdownExtractionOptions> extractionOptions,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IJobQueueService jobQueue,
        IConfiguration configuration) : base(logger)
    {
        _docIntel = docIntel;
        _extractionOptions = extractionOptions;
        _dbFactory = dbFactory;
        _jobQueue = jobQueue;
        _configuration = configuration;
    }

    public override string JobType => nameof(ExtractAssistantFileMarkdownJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(ExtractAssistantFileMarkdownJob payload, CancellationToken ct)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var shadow = await context.AssistantFileMarkdownShadows
            .Include(s => s.OriginalFile)
            .ThenInclude(f => f.Assistant)
            .FirstOrDefaultAsync(s => s.OriginalAssistantFileId == payload.AssistantFileId, ct);

        if (shadow == null)
        {
            Logger.LogWarning("Shadow not found for AssistantFile {AssistantFileId}", payload.AssistantFileId);
            return false;
        }

        if (shadow.Status == MarkdownExtractionStatus.Completed)
        {
            return true; // Idempotent
        }

        var originalFile = shadow.OriginalFile;
        if (originalFile == null || originalFile.ContentBytes == null || originalFile.ContentBytes.Length == 0)
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Original file content missing";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);
            return false;
        }

        shadow.Status = MarkdownExtractionStatus.Processing;
        shadow.ErrorMessage = null;
        shadow.ProcessedAt = null;
        await context.SaveChangesAsync(ct);

        try
        {
            string markdownContent;
            using (var stream = new MemoryStream(originalFile.ContentBytes))
            {
                var fileName = originalFile.RelativePath;
                var contentType = originalFile.ContentType ?? "application/octet-stream";
                var extension = Path.GetExtension(fileName);

                // Handle plain text files directly without markdown extraction
                if (IsPlainTextFile(extension))
                {
                    stream.Position = 0;
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    markdownContent = await reader.ReadToEndAsync();
                    Logger.LogInformation("Directly read plain text file {FileName} ({Bytes} bytes)", fileName, originalFile.ContentBytes.Length);
                }
                else if (!_docIntel.IsFileTypeSupported(fileName, contentType))
                {
                    // Route to transcription if audio/video
                    // For now, mark as skipped if unsupported
                    shadow.Status = MarkdownExtractionStatus.Skipped;
                    shadow.ErrorMessage = "Unsupported file type for markdown extraction";
                    shadow.ProcessedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    return true;
                }
                else if (!_docIntel.IsFileSizeSupported(originalFile.ContentBytes.Length))
                {
                    shadow.Status = MarkdownExtractionStatus.Skipped;
                    shadow.ErrorMessage = "File too large for markdown extraction";
                    shadow.ProcessedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(ct);
                    return true;
                }
                else
                {
                    markdownContent = await _docIntel.ExtractMarkdownAsync(stream, fileName, ct);
                }
            }

            if (string.IsNullOrEmpty(markdownContent))
            {
                shadow.Status = MarkdownExtractionStatus.Skipped;
                shadow.ErrorMessage = "No content extracted";
                shadow.ProcessedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
                return true;
            }

            var contentHash = ComputeSha256(markdownContent);
            var storagePath = GetMarkdownStoragePath(originalFile.AssistantId, originalFile.VectorStoreName, contentHash);

            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            if (!File.Exists(storagePath))
            {
                await File.WriteAllTextAsync(storagePath, markdownContent, System.Text.Encoding.UTF8, ct);
            }

            shadow.ContentHash = contentHash;
            shadow.StoragePath = storagePath;
            shadow.FileSize = System.Text.Encoding.UTF8.GetByteCount(markdownContent);
            shadow.Status = MarkdownExtractionStatus.Completed;
            shadow.ProcessedAt = DateTime.UtcNow;
            shadow.ErrorMessage = null;

            await context.SaveChangesAsync(ct);

            // Chain indexing job
            await _jobQueue.EnqueueAsync(
                jobType: nameof(IndexAssistantFileMarkdownShadowJob).Replace("Job", string.Empty),
                payload: new IndexAssistantFileMarkdownShadowJob(payload.AssistantFileId),
                ct: ct);

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Extraction failed for AssistantFile {AssistantFileId}", payload.AssistantFileId);
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = ex.Message;
            shadow.ProcessedAt = DateTime.UtcNow;
            try { await context.SaveChangesAsync(ct); } catch { }
            return false;
        }
    }

    private static string ComputeSha256(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string GetMarkdownStoragePath(Guid assistantId, string? vectorStoreName, string contentHash)
    {
        var basePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path not configured");
        var prefix = contentHash.Substring(0, 2);
        var subdir = contentHash.Substring(2, 2);
        var storeName = vectorStoreName ?? "default";
        return Path.Combine(basePath, "assistants", assistantId.ToString(), "markdown", storeName, prefix, subdir, $"{contentHash}.md");
    }

    private static bool IsPlainTextFile(string extension)
    {
        var plainTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".json", ".xml", ".puml", ".yaml", ".yml", ".csv", ".sql", 
            ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".h", ".c", ".hpp", ".css", 
            ".html", ".htm", ".go", ".rs", ".rb", ".php", ".sh", ".bat", ".ps1", 
            ".r", ".scala", ".kt", ".swift", ".m", ".mm", ".log", ".config", ".ini"
        };
        return plainTextExtensions.Contains(extension);
    }
}

public record ExtractAssistantFileMarkdownJob(Guid AssistantFileId);



