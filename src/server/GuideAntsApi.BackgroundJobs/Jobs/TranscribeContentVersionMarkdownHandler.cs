using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.DataModel.Utilities;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class TranscribeContentVersionMarkdownHandler : JobHandlerBase<TranscribeContentVersionMarkdownJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ITranscriptionAdapter _transcription;
    private readonly IJobQueueService _jobQueue;
    private readonly IConfiguration _configuration;
    private readonly string _storageRoot;

    public TranscribeContentVersionMarkdownHandler(
        ILogger<TranscribeContentVersionMarkdownHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ITranscriptionAdapter transcription,
        IJobQueueService jobQueue,
        IConfiguration configuration) : base(logger)
    {
        _dbFactory = dbFactory;
        _transcription = transcription;
        _jobQueue = jobQueue;
        _configuration = configuration;
        _storageRoot = configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
    }

    public override string JobType => nameof(TranscribeContentVersionMarkdownJob).Replace("Job", string.Empty);

    public override async Task<bool> HandleAsync(TranscribeContentVersionMarkdownJob payload, CancellationToken cancellationToken)
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

        var version = shadow.OriginalVersion;
        if (version == null || string.IsNullOrEmpty(version.StoragePath) ||
            !StoragePathCompatibility.TryResolveExistingFilePath(version.StoragePath, _storageRoot, out var sourcePath))
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Original file missing";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return false;
        }

        // Check supported types/sizes for transcription
        if (!_transcription.IsAudioOrVideoSupported(version.FileName, version.ContentType))
        {
            shadow.Status = MarkdownExtractionStatus.Skipped;
            shadow.ErrorMessage = "Unsupported file type for transcription";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!_transcription.IsFileSizeSupported(version.FileSize))
        {
            shadow.Status = MarkdownExtractionStatus.Skipped;
            shadow.ErrorMessage = "File too large";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        try
        {
            string markdown;
            await using (var fileStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                markdown = await _transcription.TranscribeToMarkdownAsync(fileStream, version.FileName, version.ContentType, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                shadow.Status = MarkdownExtractionStatus.Skipped;
                shadow.ErrorMessage = "No content extracted";
                shadow.ProcessedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }

            var contentHash = ComputeSha256(markdown);
            var projectSlug = await context.Projects
                .Where(p => p.Id == version.ContentFile.ProjectId)
                .Select(p => p.Slug)
                .FirstOrDefaultAsync(cancellationToken) ?? version.ContentFile.ProjectId.ToString();
            var storagePath = GetMarkdownStoragePath(projectSlug, version.ContentFile.ProjectId, contentHash);

            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            if (!File.Exists(storagePath))
            {
                await File.WriteAllTextAsync(storagePath, markdown, System.Text.Encoding.UTF8, cancellationToken);
            }

            shadow.ContentHash = contentHash;
            shadow.StoragePath = storagePath;
            shadow.FileSize = System.Text.Encoding.UTF8.GetByteCount(markdown);
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
            Logger.LogError(ex, "Transcription failed for ContentFileVersion {Id}", payload.ContentFileVersionId);
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


