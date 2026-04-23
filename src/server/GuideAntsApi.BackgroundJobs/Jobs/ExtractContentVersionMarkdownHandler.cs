using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class ExtractContentVersionMarkdownHandler : JobHandlerBase<ExtractContentVersionMarkdownJob>
{
    private readonly IDocumentIntelligenceService _docIntel;
    private readonly IOptions<MarkdownExtractionOptions> _extractionOptions;
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IJobQueueService _jobQueue;
    private readonly IConfiguration _configuration;
    private readonly string _storageRoot;

    public ExtractContentVersionMarkdownHandler(
        ILogger<ExtractContentVersionMarkdownHandler> logger,
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
        _storageRoot = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    public override string JobType => nameof(ExtractContentVersionMarkdownJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(ExtractContentVersionMarkdownJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var shadow = await context.ContentFileMarkdownShadows
            .Include(s => s.OriginalVersion)
            .ThenInclude(v => v.ContentFile)
            .FirstOrDefaultAsync(s => s.OriginalContentFileVersionId == payload.ContentFileVersionId, cancellationToken);

        if (shadow == null)
        {
            Logger.LogWarning("Shadow not found for ContentFileVersion {Id}", payload.ContentFileVersionId);
            return false;
        }

        if (shadow.Status == MarkdownExtractionStatus.Completed)
        {
            return true; // Idempotent
        }

        var originalVersion = shadow.OriginalVersion;
        if (originalVersion == null || string.IsNullOrEmpty(originalVersion.StoragePath) ||
            !StoragePathCompatibility.TryResolveExistingFilePath(originalVersion.StoragePath, _storageRoot, out var originalPath))
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Original file missing";
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
            await using (var fileStream = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (!_docIntel.IsFileTypeSupported(originalVersion.FileName, originalVersion.ContentType))
                {
                    // Route audio/video to transcription job path via queue
                    await _jobQueue.EnqueueAsync(
                        jobType: nameof(TranscribeContentVersionMarkdownJob).Replace("Job", string.Empty),
                        payload: new TranscribeContentVersionMarkdownJob(payload.ContentFileVersionId),
                        ct: cancellationToken);

                    shadow.Status = MarkdownExtractionStatus.Pending;
                    shadow.ErrorMessage = null;
                    shadow.ProcessedAt = null;
                    await context.SaveChangesAsync(cancellationToken);
                    return true; // dispatched to transcription
                }

                if (!_docIntel.IsFileSizeSupported(originalVersion.FileSize))
                {
                    shadow.Status = MarkdownExtractionStatus.Skipped;
                    shadow.ErrorMessage = "File too large";
                    shadow.ProcessedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync(cancellationToken);
                    return true;
                }

                markdownContent = await _docIntel.ExtractMarkdownAsync(fileStream, originalVersion.FileName, cancellationToken);
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
            var projectSlug = await context.Projects
                .Where(p => p.Id == originalVersion.ContentFile.ProjectId)
                .Select(p => p.Slug)
                .FirstOrDefaultAsync(cancellationToken) ?? originalVersion.ContentFile.ProjectId.ToString();
            var storagePath = GetMarkdownStoragePath(projectSlug, originalVersion.ContentFile.ProjectId, contentHash);

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
                jobType: nameof(IndexContentMarkdownShadowJob).Replace("Job", string.Empty),
                payload: new IndexContentMarkdownShadowJob(payload.ContentFileVersionId),
                ct: cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Extraction failed for ContentFileVersion {Id}", payload.ContentFileVersionId);
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

    private string GetMarkdownStoragePath(string projectSlug, Guid projectId, string contentHash)
    {
        var basePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        var prefix = contentHash.Substring(0, 2);
        var subdir = contentHash.Substring(2, 2);
        var named = Path.Combine(basePath, "projects", projectSlug, "content", prefix, subdir, $"{contentHash}.md");
        if (File.Exists(named))
        {
            return named;
        }

        return Path.Combine(basePath, "projects", projectId.ToString(), "content", prefix, subdir, $"{contentHash}.md");
    }
}



