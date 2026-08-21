using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Streaming;
using AntRunner.Chat.Abstractions;
using GuideAnts.Usage;
using GuideAntsApi.Services.Conversations.Queries;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.DataModel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace GuideAntsApi.Tests.TestUtils;

internal static class ConversationTestServices
{
    public static (
        ConversationQueryService Query,
        ConversationCommandService Command,
        ConversationHistoryBuilder History,
        AttachmentContentService Attachments) Create(
        TestServiceScopeFactory scopeFactory,
        IContextOptionsService? contextOptionsService = null,
        IMarkdownExtractionService? markdownExtractionService = null,
        IConfiguration? configuration = null)
    {
        var contextOptions = contextOptionsService ?? Mock.Of<IContextOptionsService>();
        var attachments = new AttachmentContentService(
            scopeFactory,
            Microsoft.Extensions.Options.Options.Create(new MarkdownAttachmentOptions()),
            notebookFileService: null,
            markdownExtractionService,
            Mock.Of<ILogger<AttachmentContentService>>(),
            configuration);

        var history = new ConversationHistoryBuilder(
            scopeFactory,
            contextOptions,
            attachments,
            Mock.Of<ILogger<ConversationHistoryBuilder>>());

        var query = new ConversationQueryService(scopeFactory);
        var command = new ConversationCommandService(
            scopeFactory,
            Mock.Of<ILogger<ConversationCommandService>>());

        return (query, command, history, attachments);
    }

    public static (ConversationPersistence Persistence, ConversationUsageReporter UsageReporter) CreatePersistence(
        TestServiceScopeFactory scopeFactory,
        IUsageRecorder? usageRecorder = null)
    {
        var recorder = usageRecorder ?? Mock.Of<IUsageRecorder>();
        var persistence = new ConversationPersistence(scopeFactory, Mock.Of<ILogger<ConversationPersistence>>());
        var usageReporter = new ConversationUsageReporter(
            scopeFactory,
            recorder,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<ILogger<ConversationUsageReporter>>());
        return (persistence, usageReporter);
    }

    public static ConversationService CreateConversationService(
        TestServiceScopeFactory scopeFactory,
        IChatModelResolver chatModelResolver,
        IConversationQueryService queryService,
        IConversationCommandService commandService,
        IConversationHistoryBuilder historyBuilder,
        IAttachmentContentService attachmentService,
        IConversationPersistence persistence,
        IConversationUsageReporter usageReporter,
        IChatCompletionClientFactory chatClientFactory,
        IDistributedConversationLock? distributedLock = null,
        IConversationBroadcastHub? broadcastHub = null,
        INotebookFileSyncService? notebookFileSyncService = null,
        IToolOAuthService? toolOAuthService = null,
        ILogger<ConversationService>? logger = null)
    {
        var lockService = distributedLock ?? Mock.Of<IDistributedConversationLock>();
        var hub = broadcastHub ?? Mock.Of<IConversationBroadcastHub>();
        var lockCoordinator = new ConversationStreamLockCoordinator(lockService);
        var streamPolicy = new PrivateConversationStreamPolicy(
            hub,
            lockCoordinator,
            scopeFactory,
            Mock.Of<ILogger<PrivateConversationStreamPolicy>>());
        var streamEngine = new ConversationStreamEngine(
            Mock.Of<IHttpClientFactory>(),
            chatClientFactory,
            persistence,
            usageReporter,
            scopeFactory,
            Mock.Of<ILogger<ConversationStreamEngine>>(),
            notebookFileSyncService);
        var streamRunRegistry = new ConversationStreamRunRegistry();
        var undoService = new ConversationUndoService(
            lockService,
            hub,
            streamPolicy,
            streamRunRegistry,
            scopeFactory,
            Mock.Of<ILogger<ConversationUndoService>>());

        return new ConversationService(
            scopeFactory,
            persistence,
            chatModelResolver,
            queryService,
            commandService,
            historyBuilder,
            attachmentService,
            Mock.Of<INotebookFileService>(),
            undoService,
            streamPolicy,
            streamEngine,
            streamRunRegistry,
            hub,
            logger ?? Mock.Of<ILogger<ConversationService>>(),
            toolOAuthService);
    }

    public static PublishedConversationService CreatePublishedConversationService(
        TestServiceScopeFactory scopeFactory,
        IChatModelResolver chatModelResolver,
        IConversationQueryService queryService,
        IConversationCommandService commandService,
        IConversationHistoryBuilder historyBuilder,
        IAttachmentContentService attachmentService,
        IConversationPersistence persistence,
        IConversationUsageReporter usageReporter,
        IChatCompletionClientFactory chatClientFactory,
        IHttpClientFactory? httpClientFactory = null,
        IConfiguration? configuration = null,
        ILogger<PublishedConversationService>? logger = null)
    {
        var distributedLock = new Mock<IDistributedConversationLock>();
        distributedLock
            .Setup(l => l.TryAcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid conversationId, string userName, CancellationToken _) =>
                LockAcquisitionResult.Acquired(new ConversationLock
                {
                    ConversationId = conversationId,
                    LockedByUserName = userName,
                    LockedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                }));
        distributedLock
            .Setup(l => l.RenewLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        distributedLock
            .Setup(l => l.ReleaseLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var lockCoordinator = new ConversationStreamLockCoordinator(distributedLock.Object);
        var streamPolicy = new PublishedConversationStreamPolicy(
            scopeFactory,
            lockCoordinator,
            Mock.Of<ILogger<PublishedConversationStreamPolicy>>());
        var streamEngine = new ConversationStreamEngine(
            httpClientFactory ?? Mock.Of<IHttpClientFactory>(),
            chatClientFactory,
            persistence,
            usageReporter,
            scopeFactory,
            Mock.Of<ILogger<ConversationStreamEngine>>());

        var undoLockService = Mock.Of<IDistributedConversationLock>();
        var privateStreamPolicy = new PrivateConversationStreamPolicy(
            Mock.Of<IConversationBroadcastHub>(),
            new ConversationStreamLockCoordinator(undoLockService),
            scopeFactory,
            Mock.Of<ILogger<PrivateConversationStreamPolicy>>());
        var undoService = new ConversationUndoService(
            undoLockService,
            Mock.Of<IConversationBroadcastHub>(),
            privateStreamPolicy,
            new ConversationStreamRunRegistry(),
            scopeFactory,
            Mock.Of<ILogger<ConversationUndoService>>());

        return new PublishedConversationService(
            scopeFactory,
            httpClientFactory ?? Mock.Of<IHttpClientFactory>(),
            persistence,
            logger ?? Mock.Of<ILogger<PublishedConversationService>>(),
            chatModelResolver,
            queryService,
            commandService,
            undoService,
            historyBuilder,
            attachmentService,
            streamPolicy,
            streamEngine,
            new ConversationStreamRunRegistry(),
            configuration);
    }
}
