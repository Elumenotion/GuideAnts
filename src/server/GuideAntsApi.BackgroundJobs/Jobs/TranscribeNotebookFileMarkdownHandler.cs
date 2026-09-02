using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.BackgroundJobs.Jobs;

public sealed class TranscribeNotebookFileMarkdownHandler : JobHandlerBase<TranscribeNotebookFileMarkdownJob>
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ITranscriptionAdapter _transcription;
    private readonly IJobQueueService _jobQueue;
    private readonly IConfiguration _configuration;

    public TranscribeNotebookFileMarkdownHandler(
        ILogger<TranscribeNotebookFileMarkdownHandler> logger,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ITranscriptionAdapter transcription,
        IJobQueueService jobQueue,
        IConfiguration configuration) : base(logger)
    {
        _dbFactory = dbFactory;
        _transcription = transcription;
        _jobQueue = jobQueue;
        _configuration = configuration;
    }

    public override string JobType => nameof(TranscribeNotebookFileMarkdownJob).Replace("Job", string.Empty);

    public override async Task<JobExecutionResult> HandleAsync(TranscribeNotebookFileMarkdownJob payload, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var shadow = await context.NotebookFileMarkdownShadows
            .Include(s => s.OriginalFile)
            .ThenInclude(f => f.Notebook)
            .FirstOrDefaultAsync(s => s.OriginalNotebookFileId == payload.NotebookFileId, cancellationToken);

        if (shadow == null)
        {
            Logger.LogWarning("Notebook shadow not found for {Id}", payload.NotebookFileId);
            return JobExecutionResult.PermanentMissingInput("Notebook shadow not found");
        }

        if (shadow.Status == MarkdownExtractionStatus.Completed)
        {
            Logger.LogDebug("Notebook shadow already completed for {Id}; skipping transcription", payload.NotebookFileId);
            return JobExecutionResult.Success();
        }

        var originalFile = shadow.OriginalFile;
        if (originalFile == null)
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Original file missing";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.PermanentMissingInput("Original file missing");
        }

        // Resolve physical path
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
            notebookRoot = Path.Combine(basePath, originalFile.Notebook.ProjectId.ToString(), "notebooks", originalFile.NotebookId.ToString());
            physicalPath = Path.Combine(notebookRoot, originalFile.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        }

        if (!File.Exists(physicalPath))
        {
            shadow.Status = MarkdownExtractionStatus.Failed;
            shadow.ErrorMessage = "Notebook file not found";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.PermanentMissingInput("Notebook file not found");
        }

        var fileName = Path.GetFileName(originalFile.RelativePath);
        var contentType = GetContentType(fileName);

        if (!_transcription.IsAudioOrVideoSupported(fileName, contentType))
        {
            shadow.Status = MarkdownExtractionStatus.Skipped;
            shadow.ErrorMessage = "Unsupported file type for transcription";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Success();
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!_transcription.IsFileSizeSupported(fileInfo.Length))
        {
            shadow.Status = MarkdownExtractionStatus.Skipped;
            shadow.ErrorMessage = "File too large";
            shadow.ProcessedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Success();
        }

        try
        {
            string markdown;
            await using (var fileStream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                markdown = await _transcription.TranscribeToMarkdownAsync(fileStream, fileName, contentType, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(markdown))
            {
                shadow.Status = MarkdownExtractionStatus.Skipped;
                shadow.ErrorMessage = "No content extracted";
                shadow.ProcessedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }

            var contentHash = ComputeSha256(markdown);
            var storagePath = GetNotebookMarkdownStoragePath(projectSlug, notebookSlug, contentHash);

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
                jobType: nameof(IndexNotebookMarkdownShadowJob).Replace("Job", string.Empty),
                payload: new IndexNotebookMarkdownShadowJob(payload.NotebookFileId),
                ct: cancellationToken);

            return JobExecutionResult.Success();
        }
        catch (Exception ex)
        {
            var result = TranscriptionJobFailureClassifier.Classify(ex, cancellationToken);
            if (result.FailureClass == JobFailureClass.ShutdownCancellation)
            {
                Logger.LogInformation(
                    "Transcription cancelled for NotebookFile {Id}: {Message}",
                    payload.NotebookFileId,
                    ex.Message);
                return result;
            }

            Logger.LogError(ex, "Transcription failed for NotebookFile {Id}", payload.NotebookFileId);
            shadow.Status = result.FailureClass == JobFailureClass.PermanentMissingInput
                ? MarkdownExtractionStatus.Failed
                : shadow.Status;
            if (result.FailureClass == JobFailureClass.PermanentMissingInput)
            {
                shadow.ErrorMessage = ex.Message;
                shadow.ProcessedAt = DateTime.UtcNow;
                try { await context.SaveChangesAsync(cancellationToken); } catch { }
            }

            return result;
        }
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/ogg",
            ".flac" => "audio/flac",
            ".wma" => "audio/x-ms-wma",
            ".amr" => "audio/amr",
            ".webm" => "audio/webm",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".wmv" => "video/x-ms-wmv",
            ".mkv" => "video/x-matroska",
            ".flv" => "video/x-flv",
            ".m4v" => "video/x-m4v",
            _ => "application/octet-stream"
        };
    }

    private static string ComputeSha256(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string GetNotebookMarkdownStoragePath(string projectSlug, string notebookSlug, string contentHash)
    {
        var basePath = _configuration["FileStorage:Path"] ?? throw new InvalidOperationException("FileStorage:Path is not configured");
        var prefix = contentHash[..2];
        var subdir = contentHash[2..4];
        return Path.Combine(basePath, "projects", projectSlug, notebookSlug, "markdown", prefix, subdir, $"{contentHash}.md");
    }
}


