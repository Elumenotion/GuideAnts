using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class ExtractNotebookFileMarkdownHandler : JobHandlerBase<ExtractNotebookFileMarkdownJob>
{
    private readonly IDocumentIntelligenceService _docIntel;
    private readonly IOptions<MarkdownExtractionOptions> _extractionOptions;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IJobQueueService _jobQueue;
    private readonly IConfiguration _configuration;

    public ExtractNotebookFileMarkdownHandler(
        ILogger<ExtractNotebookFileMarkdownHandler> logger,
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

    public override string JobType => nameof(ExtractNotebookFileMarkdownJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(ExtractNotebookFileMarkdownJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var shadow = await context.NotebookFileMarkdownShadows
            .Include(s => s.OriginalFile)
            .ThenInclude(f => f.Notebook)
            .FirstOrDefaultAsync(s => s.OriginalNotebookFileId == payload.NotebookFileId, cancellationToken);

        if (shadow == null)
        {
            Logger.LogWarning("Notebook shadow not found for {Id}", payload.NotebookFileId);
            return false;
        }

        if (shadow.Status == MarkdownExtractionStatus.Completed)
        {
            return true; // Idempotent
        }

        var originalFile = shadow.OriginalFile;
        if (originalFile == null)
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Original file missing";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return false;
        }

        // Determine physical path based on storage layout
        var basePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        var projectSlug = await context.Projects
            .Where(p => p.Id == originalFile.Notebook.ProjectId)
            .Select(p => p.Slug)
            .FirstOrDefaultAsync(cancellationToken) ?? originalFile.Notebook.ProjectId.ToString();
        var notebookSlug = string.IsNullOrWhiteSpace(originalFile.Notebook.Slug)
            ? originalFile.NotebookId.ToString()
            : originalFile.Notebook.Slug;

        var notebookRoot = Path.Combine(basePath, projectSlug, notebookSlug);
        var physicalPath = Path.Combine(notebookRoot, originalFile.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        if (!File.Exists(physicalPath))
        {
            // Legacy fallback during migration.
            notebookRoot = Path.Combine(basePath, originalFile.Notebook.ProjectId.ToString(), "notebooks", originalFile.NotebookId.ToString());
            physicalPath = Path.Combine(notebookRoot, originalFile.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        }

        if (!File.Exists(physicalPath))
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Notebook file not found";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return false;
        }

        shadow.Status = MarkdownExtractionStatus.Processing;
        shadow.ErrorMessage = null;
        shadow.ProcessedAt = null;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            string markdownContent;
            await using (var fileStream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var fileName = Path.GetFileName(originalFile.RelativePath);
                var contentType = GetContentType(fileName);

                if (!_docIntel.IsFileTypeSupported(fileName, contentType))
                {
                    // Route audio/video to transcription job path via queue
                    await _jobQueue.EnqueueAsync(
                        jobType: nameof(TranscribeNotebookFileMarkdownJob).Replace("Job", string.Empty),
                        payload: new TranscribeNotebookFileMarkdownJob(payload.NotebookFileId),
                        ct: cancellationToken);

                    shadow.Status = MarkdownExtractionStatus.Pending;
                    shadow.ErrorMessage = null;
                    shadow.ProcessedAt = null;
                    await context.SaveChangesAsync(cancellationToken);
                    return true; // dispatched to transcription
                }

                markdownContent = await _docIntel.ExtractMarkdownAsync(fileStream, fileName, cancellationToken);
            }

            if (string.IsNullOrEmpty(markdownContent))
            {
                shadow.Status = MarkdownExtractionStatus.Skipped;
                shadow.ErrorMessage = "No content extracted";
                shadow.ProcessedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }

            var contentHash = ComputeSha256(markdownContent);
            var storagePath = GetNotebookMarkdownStoragePath(projectSlug, notebookSlug, originalFile.Notebook.ProjectId, originalFile.NotebookId, contentHash);

            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            if (!File.Exists(storagePath))
            {
                await File.WriteAllTextAsync(storagePath, markdownContent, System.Text.Encoding.UTF8, cancellationToken);
            }

            shadow.ContentHash = contentHash;
            shadow.StoragePath = storagePath;
            shadow.FileSize = System.Text.Encoding.UTF8.GetByteCount(markdownContent);
            shadow.Status = MarkdownExtractionStatus.Completed;
            shadow.ProcessedAt = DateTime.UtcNow;
            shadow.ErrorMessage = null;

            await context.SaveChangesAsync(cancellationToken);

            // Chain indexing job
            await _jobQueue.EnqueueAsync(
                jobType: nameof(IndexNotebookMarkdownShadowJob).Replace("Job", string.Empty),
                payload: new IndexNotebookMarkdownShadowJob(payload.NotebookFileId),
                ct: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Extraction failed for NotebookFile {Id}", payload.NotebookFileId);
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = ex.Message;
            shadow.ProcessedAt = DateTime.UtcNow;
            try { await context.SaveChangesAsync(cancellationToken); } catch { }
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

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".tiff" => "image/tiff",
            ".heif" => "image/heif",
            ".html" => "text/html",
            _ => "application/octet-stream"
        };
    }

    private string GetNotebookMarkdownStoragePath(string projectSlug, string notebookSlug, Guid projectId, Guid notebookId, string contentHash)
    {
        var basePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        var prefix = contentHash.Substring(0, 2);
        var subdir = contentHash.Substring(2, 2);
        var named = Path.Combine(basePath, "projects", projectSlug, notebookSlug, "markdown", prefix, subdir, $"{contentHash}.md");
        if (File.Exists(named))
        {
            return named;
        }

        return Path.Combine(basePath, "projects", projectId.ToString(), "notebooks", notebookId.ToString(), "markdown", prefix, subdir, $"{contentHash}.md");
    }
}



