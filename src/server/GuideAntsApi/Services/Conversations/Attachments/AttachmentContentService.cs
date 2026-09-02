using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Components.Sync;
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
    private readonly IAttachmentRenderCache? _renderCache;

    public AttachmentContentService(
        IServiceScopeFactory scopeFactory,
        IOptions<MarkdownAttachmentOptions> markdownAttachmentOptions,
        INotebookFileService? notebookFileService = null,
        IMarkdownExtractionService? markdownExtractionService = null,
        ILogger<AttachmentContentService>? logger = null,
        IConfiguration? configuration = null,
        IAttachmentRenderCache? renderCache = null)
    {
        _scopeFactory = scopeFactory;
        _markdownAttachmentOptions = markdownAttachmentOptions ?? throw new ArgumentNullException(nameof(markdownAttachmentOptions));
        _notebookFileService = notebookFileService;
        _markdownExtractionService = markdownExtractionService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration;
        _storagePath = configuration?["FileStorage:Path"] ?? string.Empty;
        _renderCache = renderCache;
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

        var seenCanonicalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderIndex = 0;
        var insertedCount = 0;

        foreach (var attachment in attachments)
        {
            var normalizedPath = NormalizeRelativePath(attachment.RelativePath);
            Guid? notebookFileId = null;

            if (attachment.NotebookFileId.HasValue)
            {
                var notebookFile = await db.NotebookFiles
                    .FirstOrDefaultAsync(
                        f => f.Id == attachment.NotebookFileId.Value && f.NotebookId == notebookId,
                        cancellationToken);

                if (notebookFile == null)
                {
                    _logger.LogWarning(
                        "Attachment file {NotebookFileId} not found or doesn't belong to notebook {NotebookId}",
                        LogValueSanitizer.Sanitize(attachment.NotebookFileId),
                        LogValueSanitizer.Sanitize(notebookId));
                    continue;
                }

                notebookFileId = notebookFile.Id;
            }
            else if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                var notebookFile = await FindNotebookFileByRelativePathAsync(
                    db,
                    notebookId,
                    normalizedPath,
                    cancellationToken);
                notebookFileId = notebookFile?.Id;
            }
            else
            {
                _logger.LogWarning(
                    "Skipping attachment with neither a file id nor a relative path for message {MessageId}",
                    userMessageId);
                continue;
            }

            var canonicalKey = notebookFileId.HasValue
                ? $"id:{notebookFileId.Value:N}"
                : $"path:{normalizedPath}";
            if (!seenCanonicalKeys.Add(canonicalKey))
            {
                _logger.LogDebug(
                    "Skipping duplicate attachment for message {MessageId}: canonicalKey={CanonicalKey}",
                    userMessageId,
                    LogValueSanitizer.Sanitize(canonicalKey));
                continue;
            }

            db.MessageAttachments.Add(new MessageAttachment
            {
                MessageId = userMessageId,
                NotebookFileId = notebookFileId,
                RelativePath = notebookFileId.HasValue ? null : normalizedPath,
                UploadType = attachment.UploadType,
                Type = AttachmentType.Referenced,
                OrderIndex = orderIndex++,
                Created = DateTime.UtcNow
            });

            insertedCount++;
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added {Count} attachments to message {MessageId}",
            insertedCount, userMessageId);
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

            if (_renderCache != null && _renderCache.TryGet(notebookFile.Id, notebookFile.LastModifiedUtc.Ticks, out var cached))
            {
                return cached;
            }

            var rendered = await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
                notebookFile,
                _notebookFileService,
                _markdownExtractionService,
                _storagePath,
                cancellationToken,
                _markdownAttachmentOptions.Value.MaxInlineCharacters,
                _logger);

            _renderCache?.Set(notebookFile.Id, notebookFile.LastModifiedUtc.Ticks, rendered);
            return rendered;
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

    public async Task<List<ChatContent>> ExpandAttachmentToChatContentsAsync(
        MessageAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await ExpandAttachmentToChatContentsAsync(db, attachment, cancellationToken);
    }

    public async Task<List<ChatContent>> ExpandAttachmentToChatContentsAsync(
        ApplicationDbContext db,
        MessageAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (attachment.NotebookFileId.HasValue)
        {
            return await CreateOpenAiContentFromNotebookFileAsync(
                db,
                attachment.NotebookFileId.Value,
                cancellationToken);
        }

        var normalizedPath = NormalizeRelativePath(attachment.RelativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return [];
        }

        var path = ContextOptionFilesResolver.ToCwdRelativePath(normalizedPath, isPublished: false);
        var label = attachment.UploadType == ContentUploadType.Folder
            ? $"Attachment (folder): {path}"
            : $"Attachment: {path}";
        return [new ChatContent(label)];
    }

    private static string? NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return relativePath.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static async Task<NotebookFile?> FindNotebookFileByRelativePathAsync(
        ApplicationDbContext db,
        Guid notebookId,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        var file = await db.NotebookFiles
            .FirstOrDefaultAsync(
                f => f.NotebookId == notebookId && f.RelativePath == normalizedPath,
                cancellationToken);
        if (file != null)
        {
            return file;
        }

        foreach (var alternativePath in NotebookPathResolver.GetAlternativePaths(normalizedPath))
        {
            file = await db.NotebookFiles
                .FirstOrDefaultAsync(
                    f => f.NotebookId == notebookId && f.RelativePath == alternativePath,
                    cancellationToken);
            if (file != null)
            {
                return file;
            }
        }

        var notebookFiles = await db.NotebookFiles
            .Where(f => f.NotebookId == notebookId)
            .ToListAsync(cancellationToken);

        return notebookFiles.FirstOrDefault(f =>
            string.Equals(f.RelativePath, normalizedPath, StringComparison.OrdinalIgnoreCase)
            || NotebookPathResolver.GetAlternativePaths(normalizedPath)
                .Any(path => string.Equals(f.RelativePath, path, StringComparison.OrdinalIgnoreCase))
            || (normalizedPath.Contains('/')
                && f.RelativePath.EndsWith("/" + normalizedPath, StringComparison.OrdinalIgnoreCase)));

    }

    public async Task<List<ChatContent>> CreateOpenAiContentFromLoadedFileAsync(
        NotebookFile notebookFile,
        CancellationToken cancellationToken = default)
    {
        var contents = new List<ChatContent>();
        if (_notebookFileService == null) return contents;

        try
        {
            if (_renderCache != null && _renderCache.TryGet(notebookFile.Id, notebookFile.LastModifiedUtc.Ticks, out var cached))
            {
                return cached;
            }

            var rendered = await AttachmentMessageBuilder.CreateContentFromNotebookFileAsync(
                notebookFile,
                _notebookFileService,
                _markdownExtractionService,
                _storagePath,
                cancellationToken,
                _markdownAttachmentOptions.Value.MaxInlineCharacters,
                _logger);

            _renderCache?.Set(notebookFile.Id, notebookFile.LastModifiedUtc.Ticks, rendered);
            return rendered;
        }
        catch (GuideAntsApi.Exceptions.AttachmentNotReadyException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating OpenAI content for file {NotebookFileId}", notebookFile.Id);
            return contents;
        }
    }
}
