using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.BackgroundJobs.Jobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.DataModel.Utilities;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Core;

// Job types defined in BackgroundJobs project
using ExtractAssistantFileMarkdownJob = GuideAntsApi.BackgroundJobs.Jobs.ExtractAssistantFileMarkdownJob;

namespace GuideAntsApi.Services.Components
{
    public class MarkdownExtractionService : IMarkdownExtractionService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IJobQueueService _jobQueueService;
        private readonly MarkdownExtractionOptions _extractionOptions;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MarkdownExtractionService> _logger;
        private readonly string _storagePath;

        public MarkdownExtractionService(
            IServiceScopeFactory scopeFactory,
            IJobQueueService jobQueueService,
            IOptions<MarkdownExtractionOptions> extractionOptions,
            IConfiguration configuration,
            ILogger<MarkdownExtractionService> logger)
        {
            _scopeFactory = scopeFactory;
            _jobQueueService = jobQueueService;
            _extractionOptions = extractionOptions.Value;
            _configuration = configuration;
            _logger = logger;
            _storagePath = _configuration["FileStorage:Path"] ?? throw new ("FileStorage:Path is not configured");
        }
        
        /// <summary>
        /// Creates a new scope and returns the DbContext. Use with 'using' statement.
        /// </summary>
        private IServiceScope CreateDbScope() => _scopeFactory.CreateScope();
        
        /// <summary>
        /// Gets the DbContext from a scope. Use with CreateDbScope().
        /// </summary>
        private static ApplicationDbContext GetDbContext(IServiceScope scope) => 
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        public async Task<ContentFileMarkdownShadow> CreateMarkdownShadowAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            // Check if shadow already exists
            var existingShadow = await context.ContentFileMarkdownShadows
                .FirstOrDefaultAsync(s => s.OriginalContentFileVersionId == contentFileVersionId, cancellationToken);

            if (existingShadow != null)
            {
                _logger.LogInformation("Markdown shadow already exists for ContentFileVersion {ContentFileVersionId}", contentFileVersionId);
                return existingShadow;
            }

            // Get the original file version
            var contentFileVersion = await context.ContentFileVersions
                .Include(v => v.ContentFile)
                .FirstOrDefaultAsync(v => v.Id == contentFileVersionId, cancellationToken);

            if (contentFileVersion == null)
            {
                throw new ArgumentException($"ContentFileVersion {contentFileVersionId} not found");
            }

            // Create pending shadow record
            var shadow = new ContentFileMarkdownShadow
            {
                OriginalContentFileVersionId = contentFileVersionId,
                ContentHash = string.Empty, // Will be set when processed
                StoragePath = string.Empty, // Will be set when processed
                FileSize = 0, // Will be set when processed
                Status = MarkdownExtractionStatus.Pending
            };

            context.ContentFileMarkdownShadows.Add(shadow);
            await context.SaveChangesAsync(cancellationToken);

            // Enqueue background job for extraction
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractContentVersionMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractContentVersionMarkdownJob(contentFileVersionId),
                ct: cancellationToken);

            _logger.LogInformation("Created pending markdown shadow and enqueued job for ContentFileVersion {ContentFileVersionId}", contentFileVersionId);
            return shadow;
        }

        public async Task<ContentFileMarkdownShadow?> GetMarkdownShadowAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            return await context.ContentFileMarkdownShadows
                .Include(s => s.OriginalVersion)
                .ThenInclude(v => v.ContentFile)
                .FirstOrDefaultAsync(s => s.OriginalContentFileVersionId == contentFileVersionId, cancellationToken);
        }

        public async Task<(Stream? Content, string? ContentType, string? FileName)?> GetMarkdownContentAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetMarkdownShadowAsync(contentFileVersionId, cancellationToken);
            
            if (shadow?.Status != MarkdownExtractionStatus.Completed || string.IsNullOrEmpty(shadow.StoragePath))
            {
                return null;
            }

            if (!StoragePathCompatibility.TryResolveExistingFilePath(shadow.StoragePath, _storagePath, out var physicalPath))
            {
                _logger.LogWarning("Markdown file not found at path: {StoragePath}", shadow.StoragePath);
                return null;
            }

            try
            {
                var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var fileName = Path.GetFileNameWithoutExtension(shadow.OriginalVersion.FileName) + ".md";
                return (stream, "text/markdown", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading markdown file: {StoragePath}", physicalPath);
                return null;
            }
        }

        public async Task<ContentFileMarkdownShadow> RetryExtractionAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetMarkdownShadowAsync(contentFileVersionId, cancellationToken);
            
            if (shadow == null)
            {
                throw new ArgumentException($"No markdown shadow found for ContentFileVersion {contentFileVersionId}");
            }

            if (shadow.Status != MarkdownExtractionStatus.Failed)
            {
                throw new InvalidOperationException($"Cannot retry extraction for shadow with status {shadow.Status}");
            }

            using var scope = CreateDbScope();
            var context = GetDbContext(scope);

            // Reset to pending for reprocessing
            shadow.Status = MarkdownExtractionStatus.Pending;
            shadow.ErrorMessage = null;
            shadow.ProcessedAt = null;

            await context.SaveChangesAsync(cancellationToken);

            // Enqueue job again
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractContentVersionMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractContentVersionMarkdownJob(contentFileVersionId),
                ct: cancellationToken);

            _logger.LogInformation("Reset markdown shadow {ShadowId} to pending and re-enqueued job", shadow.Id);
            return shadow;
        }

        public async Task<MarkdownExtractionStatus?> GetExtractionStatusAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            var shadow = await context.ContentFileMarkdownShadows
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.OriginalContentFileVersionId == contentFileVersionId, cancellationToken);

            return shadow?.Status;
        }

        // Notebook file methods
        public async Task<NotebookFileMarkdownShadow> CreateNotebookMarkdownShadowAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            // Check if shadow already exists
            var existingShadow = await context.NotebookFileMarkdownShadows
                .FirstOrDefaultAsync(s => s.OriginalNotebookFileId == notebookFileId, cancellationToken);

            if (existingShadow != null)
            {
                _logger.LogInformation("Markdown shadow already exists for NotebookFile {NotebookFileId}", notebookFileId);
                return existingShadow;
            }

            // Get the original notebook file
            var notebookFile = await context.NotebookFiles
                .Include(f => f.Notebook)
                .FirstOrDefaultAsync(f => f.Id == notebookFileId, cancellationToken);

            if (notebookFile == null)
            {
                throw new ArgumentException($"NotebookFile with ID {notebookFileId} not found", nameof(notebookFileId));
            }

            // Create shadow record
            var shadow = new NotebookFileMarkdownShadow
            {
                OriginalNotebookFileId = notebookFileId,
                ContentHash = string.Empty, // Will be set during processing
                StoragePath = string.Empty, // Will be set during processing
                FileSize = 0, // Will be set during processing
                Status = MarkdownExtractionStatus.Pending
            };

            context.NotebookFileMarkdownShadows.Add(shadow);
            await context.SaveChangesAsync(cancellationToken);

            // Enqueue background job for extraction
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractNotebookFileMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractNotebookFileMarkdownJob(notebookFileId),
                ct: cancellationToken);

            _logger.LogInformation("Created markdown shadow and enqueued job for NotebookFile {NotebookFileId}", notebookFileId);
            return shadow;
        }

        public async Task<NotebookFileMarkdownShadow?> GetNotebookMarkdownShadowAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            return await context.NotebookFileMarkdownShadows
                .FirstOrDefaultAsync(s => s.OriginalNotebookFileId == notebookFileId, cancellationToken);
        }

        public async Task<(Stream? Content, string? ContentType, string? FileName)?> GetNotebookMarkdownContentAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetNotebookMarkdownShadowAsync(notebookFileId, cancellationToken);
            if (shadow?.Status != MarkdownExtractionStatus.Completed || string.IsNullOrEmpty(shadow.StoragePath))
            {
                return null;
            }

            if (!StoragePathCompatibility.TryResolveExistingFilePath(shadow.StoragePath, _storagePath, out var physicalPath))
            {
                _logger.LogWarning("Markdown file not found at path: {StoragePath} for NotebookFile {NotebookFileId}", shadow.StoragePath, notebookFileId);
                return null;
            }

            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            var notebookFile = await context.NotebookFiles.FindAsync(notebookFileId);
            var fileName = $"{Path.GetFileNameWithoutExtension(notebookFile?.RelativePath ?? "file")}.md";

            return (new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), "text/markdown", fileName);
        }

        public async Task<NotebookFileMarkdownShadow> RetryNotebookExtractionAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetNotebookMarkdownShadowAsync(notebookFileId, cancellationToken);
            if (shadow == null)
            {
                return await CreateNotebookMarkdownShadowAsync(notebookFileId, cancellationToken);
            }

            using var scope = CreateDbScope();
            var context = GetDbContext(scope);

            // Reset status to pending
            shadow.Status = MarkdownExtractionStatus.Pending;
            shadow.ErrorMessage = null;
            shadow.ProcessedAt = null;

            await context.SaveChangesAsync(cancellationToken);

            // Re-enqueue job
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractNotebookFileMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractNotebookFileMarkdownJob(notebookFileId),
                ct: cancellationToken);
            return shadow;
        }

        public async Task<MarkdownExtractionStatus?> GetNotebookExtractionStatusAsync(Guid notebookFileId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetNotebookMarkdownShadowAsync(notebookFileId, cancellationToken);
            return shadow?.Status;
        }

        // Assistant file methods
        public async Task<AssistantFileMarkdownShadow> CreateAssistantFileMarkdownShadowAsync(Guid assistantFileId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            // Check if shadow already exists
            var existingShadow = await context.AssistantFileMarkdownShadows
                .FirstOrDefaultAsync(s => s.OriginalAssistantFileId == assistantFileId, cancellationToken);

            if (existingShadow != null)
            {
                _logger.LogInformation("Markdown shadow already exists for AssistantFile {AssistantFileId}", assistantFileId);
                return existingShadow;
            }

            // Get the original file
            var assistantFile = await context.AssistantFiles
                .Include(f => f.Assistant)
                .FirstOrDefaultAsync(f => f.Id == assistantFileId, cancellationToken);

            if (assistantFile == null)
            {
                throw new ArgumentException($"AssistantFile {assistantFileId} not found");
            }

            // Create pending shadow record
            var shadow = new AssistantFileMarkdownShadow
            {
                OriginalAssistantFileId = assistantFileId,
                ContentHash = string.Empty,
                StoragePath = string.Empty,
                FileSize = 0,
                Status = MarkdownExtractionStatus.Pending
            };

            context.AssistantFileMarkdownShadows.Add(shadow);
            await context.SaveChangesAsync(cancellationToken);

            // Enqueue background job
            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractAssistantFileMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractAssistantFileMarkdownJob(assistantFileId),
                ct: cancellationToken);

            _logger.LogInformation("Created pending markdown shadow for AssistantFile {AssistantFileId}", assistantFileId);
            return shadow;
        }

        public async Task<AssistantFileMarkdownShadow?> GetAssistantFileMarkdownShadowAsync(Guid assistantFileId, CancellationToken cancellationToken = default)
        {
            using var scope = CreateDbScope();
            var context = GetDbContext(scope);
            
            return await context.AssistantFileMarkdownShadows
                .Include(s => s.OriginalFile)
                .ThenInclude(f => f.Assistant)
                .FirstOrDefaultAsync(s => s.OriginalAssistantFileId == assistantFileId, cancellationToken);
        }

        public async Task<(Stream? Content, string? ContentType, string? FileName)?> GetAssistantFileMarkdownContentAsync(Guid assistantFileId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetAssistantFileMarkdownShadowAsync(assistantFileId, cancellationToken);
            
            if (shadow?.Status != MarkdownExtractionStatus.Completed || string.IsNullOrEmpty(shadow.StoragePath))
            {
                _logger.LogInformation("Shadow not ready: Status={Status}, StoragePath={StoragePath}", shadow?.Status, shadow?.StoragePath);
                return null;
            }

            if (!StoragePathCompatibility.TryResolveExistingFilePath(shadow.StoragePath, _storagePath, out var physicalPath))
            {
                _logger.LogWarning("Markdown file not found at path: {StoragePath} for AssistantFile {AssistantFileId}", shadow.StoragePath, assistantFileId);
                return null;
            }

            var fileName = shadow.OriginalFile?.RelativePath != null 
                ? Path.GetFileNameWithoutExtension(shadow.OriginalFile.RelativePath) + ".md"
                : $"file-{assistantFileId}.md";
            
            _logger.LogInformation("Returning markdown content from {StoragePath} as {FileName}", shadow.StoragePath, fileName);
            return (new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), "text/markdown", fileName);
        }

        public async Task<AssistantFileMarkdownShadow> RetryAssistantFileExtractionAsync(Guid assistantFileId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetAssistantFileMarkdownShadowAsync(assistantFileId, cancellationToken);
            if (shadow == null)
            {
                return await CreateAssistantFileMarkdownShadowAsync(assistantFileId, cancellationToken);
            }

            using var scope = CreateDbScope();
            var context = GetDbContext(scope);

            shadow.Status = MarkdownExtractionStatus.Pending;
            shadow.ErrorMessage = null;
            shadow.ProcessedAt = null;

            await context.SaveChangesAsync(cancellationToken);

            await _jobQueueService.EnqueueAsync(
                jobType: nameof(ExtractAssistantFileMarkdownJob).Replace("Job", string.Empty),
                payload: new ExtractAssistantFileMarkdownJob(assistantFileId),
                ct: cancellationToken);
            
            return shadow;
        }

        public async Task<MarkdownExtractionStatus?> GetAssistantFileExtractionStatusAsync(Guid assistantFileId, CancellationToken cancellationToken = default)
        {
            var shadow = await GetAssistantFileMarkdownShadowAsync(assistantFileId, cancellationToken);
            return shadow?.Status;
        }
    }
} 
