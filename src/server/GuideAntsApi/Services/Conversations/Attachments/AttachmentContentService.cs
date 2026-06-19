using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Conversations.Attachments;

public class AttachmentContentService : IAttachmentContentService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INotebookFileService? _notebookFileService;
    private readonly IMarkdownExtractionService? _markdownExtractionService;
    private readonly ILogger<AttachmentContentService> _logger;
    private readonly IConfiguration? _configuration;
    private readonly string _storagePath;
    private readonly IOptions<MarkdownAttachmentOptions> _markdownAttachmentOptions;

    public AttachmentContentService(
        IServiceScopeFactory scopeFactory,
        IOptions<MarkdownAttachmentOptions> markdownAttachmentOptions,
        INotebookFileService? notebookFileService = null,
        IMarkdownExtractionService? markdownExtractionService = null,
        ILogger<AttachmentContentService>? logger = null,
        IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _markdownAttachmentOptions = markdownAttachmentOptions ?? throw new ArgumentNullException(nameof(markdownAttachmentOptions));
        _notebookFileService = notebookFileService;
        _markdownExtractionService = markdownExtractionService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration;
        _storagePath = configuration?["FileStorage:Path"] ?? string.Empty;
    }

    public async Task AddAttachmentsToUserMessageAsync(
        Guid userMessageId,
        Guid notebookId,
        IReadOnlyList<AttachmentDto> attachments,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await AddAttachmentsToUserMessageAsync(db, userMessageId, notebookId, attachments, cancellationToken);
    }

    public async Task AddAttachmentsToUserMessageAsync(
        ApplicationDbContext db,
        Guid userMessageId,
        Guid notebookId,
        IReadOnlyList<AttachmentDto> attachments,
        CancellationToken cancellationToken = default)
    {
        if (attachments == null || attachments.Count == 0) return;

        for (int i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            if (!attachment.NotebookFileId.HasValue)
            {
                _logger.LogDebug(
                    "Skipping non-persisted path attachment for message {MessageId}: relativePath={RelativePath}",
                    userMessageId,
                    LogValueSanitizer.Sanitize(attachment.RelativePath));
                continue;
            }

            var notebookFile = await db.NotebookFiles
                .FirstOrDefaultAsync(f => f.Id == attachment.NotebookFileId.Value && f.NotebookId == notebookId, cancellationToken);

            if (notebookFile == null)
            {
                _logger.LogWarning("Attachment file {NotebookFileId} not found or doesn't belong to notebook {NotebookId}",
                    LogValueSanitizer.Sanitize(attachment.NotebookFileId), LogValueSanitizer.Sanitize(notebookId));
                continue;
            }

            var messageAttachment = new MessageAttachment
            {
                MessageId = userMessageId,
                NotebookFileId = attachment.NotebookFileId.Value,
                Type = AttachmentType.Referenced,
                OrderIndex = i,
                Created = DateTime.UtcNow
            };

            db.MessageAttachments.Add(messageAttachment);
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added {Count} attachments to message {MessageId}",
            attachments.Count, userMessageId);
    }

    public async Task<List<ChatMessage>> CreateOpenAiMessagesFromNotebookFileAsync(
        Guid notebookFileId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await CreateOpenAiMessagesFromNotebookFileAsync(db, notebookFileId, cancellationToken);
    }

    public async Task<List<ChatMessage>> CreateOpenAiMessagesFromNotebookFileAsync(
        ApplicationDbContext db,
        Guid notebookFileId,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();
        if (_notebookFileService == null) return messages;

        try
        {
            var notebookFile = await db.NotebookFiles
                .Include(nf => nf.Notebook)
                .FirstOrDefaultAsync(nf => nf.Id == notebookFileId, cancellationToken);
            if (notebookFile == null) return messages;

            return await AttachmentMessageBuilder.CreateMessagesFromNotebookFileAsync(
                notebookFile,
                _notebookFileService,
                _markdownExtractionService,
                _storagePath,
                cancellationToken,
                _markdownAttachmentOptions.Value.MaxInlineCharacters,
                _logger);
        }
        catch (GuideAntsApi.Exceptions.AttachmentNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating OpenAI messages for file {NotebookFileId}", notebookFileId);
            return messages;
        }
    }

    public async Task<List<ChatContent>> CreateOpenAiContentFromNotebookFileAsync(
        Guid notebookFileId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await CreateOpenAiContentFromNotebookFileAsync(db, notebookFileId, cancellationToken);
    }

    public async Task<List<ChatContent>> CreateOpenAiContentFromNotebookFileAsync(
        ApplicationDbContext db,
        Guid notebookFileId,
        CancellationToken cancellationToken = default)
    {
        var contents = new List<ChatContent>();
        if (_notebookFileService == null) return contents;

        try
        {
            var notebookFile = await db.NotebookFiles
                .Include(nf => nf.Notebook)
                .FirstOrDefaultAsync(nf => nf.Id == notebookFileId, cancellationToken);
            if (notebookFile == null) return contents;

            return await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
                notebookFile,
                _notebookFileService,
                _markdownExtractionService,
                _storagePath,
                cancellationToken,
                _markdownAttachmentOptions.Value.MaxInlineCharacters,
                _logger);
        }
        catch (GuideAntsApi.Exceptions.AttachmentNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating OpenAI content for file {NotebookFileId}", notebookFileId);
            return contents;
        }
    }
}
