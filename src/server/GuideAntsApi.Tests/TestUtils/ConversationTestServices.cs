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
        var streamPolicy = new PrivateConversationStreamPolicy(
            hub,
            lockService,
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
        var undoService = new ConversationUndoService(
            lockService,
            hub,
            streamPolicy,
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
        var streamPolicy = new PublishedConversationStreamPolicy(scopeFactory);
        var streamEngine = new ConversationStreamEngine(
            httpClientFactory ?? Mock.Of<IHttpClientFactory>(),
            chatClientFactory,
            persistence,
            usageReporter,
            scopeFactory,
            Mock.Of<ILogger<ConversationStreamEngine>>());

        var privateStreamPolicy = new PrivateConversationStreamPolicy(
            Mock.Of<IConversationBroadcastHub>(),
            Mock.Of<IDistributedConversationLock>(),
            scopeFactory,
            Mock.Of<ILogger<PrivateConversationStreamPolicy>>());
        var undoService = new ConversationUndoService(
            Mock.Of<IDistributedConversationLock>(),
            Mock.Of<IConversationBroadcastHub>(),
            privateStreamPolicy,
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
            configuration);
    }
}
