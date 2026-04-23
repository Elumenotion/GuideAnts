using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Core
{
    public interface IMarkdownExtractionService
    {
        /// <summary>
        /// Creates a markdown shadow for a content file version (queues for background processing)
        /// </summary>
        Task<ContentFileMarkdownShadow> CreateMarkdownShadowAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the markdown shadow for a content file version
        /// </summary>
        Task<ContentFileMarkdownShadow?> GetMarkdownShadowAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the markdown content stream for a content file version
        /// </summary>
        Task<(Stream? Content, string? ContentType, string? FileName)?> GetMarkdownContentAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default);


        /// <summary>
        /// Retries a failed markdown extraction
        /// </summary>
        Task<ContentFileMarkdownShadow> RetryExtractionAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the extraction status for a content file version
        /// </summary>
        Task<MarkdownExtractionStatus?> GetExtractionStatusAsync(Guid contentFileVersionId, CancellationToken cancellationToken = default);

        // Notebook file methods
        /// <summary>
        /// Creates a markdown shadow for a notebook file (queues for background processing)
        /// </summary>
        Task<NotebookFileMarkdownShadow> CreateNotebookMarkdownShadowAsync(Guid notebookFileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the markdown shadow for a notebook file
        /// </summary>
        Task<NotebookFileMarkdownShadow?> GetNotebookMarkdownShadowAsync(Guid notebookFileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the markdown content stream for a notebook file
        /// </summary>
        Task<(Stream? Content, string? ContentType, string? FileName)?> GetNotebookMarkdownContentAsync(Guid notebookFileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retries a failed notebook markdown extraction
        /// </summary>
        Task<NotebookFileMarkdownShadow> RetryNotebookExtractionAsync(Guid notebookFileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the extraction status for a notebook file
        /// </summary>
        Task<MarkdownExtractionStatus?> GetNotebookExtractionStatusAsync(Guid notebookFileId, CancellationToken cancellationToken = default);

        
    }
} 