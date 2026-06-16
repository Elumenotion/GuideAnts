using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Real-SQL integration coverage for the streaming generation paths in
/// <see cref="PublishedConversationService"/>. These tests drive the published streaming
/// entrypoints (<c>SendMessageStreamAsync</c> / <c>ResumeAfterExternalToolResultsStreamAsync</c>)
/// against the integration host, which swaps in <see cref="FakeChatCompletionClientFactory"/>
/// (emits "Test assistant response."). They assert both the streamed SSE events and the
/// persisted messages/turns produced by the background producer, plus the synchronous
/// validation/error branches.
/// </summary>
[TestClass]
public sealed class PublishedConversationStreamingTests : BaseEndpointTest
{
    private const string ModelId = "gpt-4.1";
    private const string FakeAssistantText = "Test assistant response.";

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        FakeChatCompletionBehavior.Instance.Reset();
        SetupAuthentication();
    }

    // PublishedGuides reference Notebooks; remove them before the base cleanup deletes notebooks.
    protected override async Task CleanDatabaseAsync()
    {
        if (SharedFactory != null)
        {
            using var scope = SharedFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
        }

        await base.CleanDatabaseAsync();
    }

    private static IPublishedConversationService ResolveService(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IPublishedConversationService>();

    private sealed record SeededConversation(Guid ProjectId, Guid NotebookId, Guid GuideId, Guid ConversationId);

    private static async Task<SeededConversation> SeedConversationAsync(ApplicationDbContext db)
    {
        // Pin to the harness-seeded "Template Guide" which has ModelId = gpt-4.1. The resume path
        // resolves the chat model from the guide's own definition (no request override), so an
        // arbitrary bootstrap guide without a configured model would be non-deterministically picked.
        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive && a.Name == "Template Guide")
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"Pub Stream Project {Guid.NewGuid():N}",
            Slug = $"pubstream-{Guid.NewGuid():N}",
            Description = "integration",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"Pub Stream Notebook {Guid.NewGuid():N}",
            Slug = $"pubstreamnb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);

        var conversation = new NotebookConversation
        {
            NotebookId = notebook.Id,
            Title = "Streaming conversation"
        };
        db.NotebookConversations.Add(conversation);
        await db.SaveChangesAsync();

        return new SeededConversation(project.Id, notebook.Id, guideId, conversation.Id);
    }

    private static async Task<List<StreamingEvent>> CollectAsync(
        IAsyncEnumerable<StreamingEvent> stream,
        int maxSeconds = 60)
    {
        var events = new List<StreamingEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds));
        await foreach (var ev in stream.WithCancellation(cts.Token))
        {
            events.Add(ev);
        }
        return events;
    }

    [TestMethod]
    public async Task SendMessageStream_Persists_user_and_assistant_messages_and_streams_content()
    {
        SeededConversation seeded;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seeded = await SeedConversationAsync(db);
        }

        List<StreamingEvent> events;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var svc = ResolveService(scope);
            var request = new SendMessageRequest { Instructions = "Hello", ModelDeploymentId = ModelId };
            events = await CollectAsync(svc.SendMessageStreamAsync(seeded.ConversationId, request, publisherId: null, externalUserIdentity: null));
        }

        // SSE events: streamed token(s), the finalized assistant message, usage, and completion.
        var eventTypes = events.Select(e => e.EventType).ToList();
        eventTypes.Should().Contain(StreamingEventTypes.Token);
        eventTypes.Should().Contain(StreamingEventTypes.Message);
        eventTypes.Should().Contain(StreamingEventTypes.Usage);
        eventTypes.Should().Contain(StreamingEventTypes.Complete);

        var messageEvent = events.First(e => e.EventType == StreamingEventTypes.Message);
        var messagePayload = JsonSerializer.Deserialize<JsonElement>(messageEvent.Payload);
        messagePayload.GetProperty("content").GetString().Should().Be(FakeAssistantText);

        // Persisted state: user message, finalized assistant message, completed turn.
        using var verifyScope = SharedFactory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var messages = await verifyDb.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == seeded.ConversationId)
            .OrderBy(m => m.MessageSequence)
            .ToListAsync();

        var userMessage = messages.Should().ContainSingle(m => m.Role == DataModelChatRole.User).Subject;
        userMessage.Content.Should().Be("Hello");
        userMessage.TurnIndex.Should().Be(1);
        userMessage.MessageSequence.Should().Be(1);

        var assistantMessage = messages.Should().ContainSingle(m => m.Role == DataModelChatRole.Assistant).Subject;
        assistantMessage.Content.Should().Be(FakeAssistantText);
        assistantMessage.IsStreaming.Should().NotBe(true);

        var turn = await verifyDb.ConversationTurns
            .SingleAsync(t => t.NotebookConversationId == seeded.ConversationId && t.TurnIndex == 1);
        turn.Status.Should().Be("completed");
        turn.Instructions.Should().Be("Hello");
        turn.ChatRunOutputJson.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task SendMessageStream_Throws_ArgumentException_when_instructions_blank_and_no_attachments()
    {
        SeededConversation seeded;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seeded = await SeedConversationAsync(db);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(verifyScope);
        var request = new SendMessageRequest { Instructions = "   ", ModelDeploymentId = ModelId };

        var act = async () => await CollectAsync(
            svc.SendMessageStreamAsync(seeded.ConversationId, request, publisherId: null, externalUserIdentity: null));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Instructions required*");
    }

    [TestMethod]
    public async Task SendMessageStream_Throws_KeyNotFound_when_conversation_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);
        var request = new SendMessageRequest { Instructions = "Hello", ModelDeploymentId = ModelId };

        var act = async () => await CollectAsync(
            svc.SendMessageStreamAsync(Guid.NewGuid(), request, publisherId: null, externalUserIdentity: null));

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Conversation not found*");
    }

    [TestMethod]
    public async Task SendMessageStream_Enforces_MaxUserMessageLength_from_published_guide()
    {
        SeededConversation seeded;
        Guid publishedGuideId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seeded = await SeedConversationAsync(db);

            var publishedGuide = new PublishedGuide
            {
                GuideId = seeded.GuideId,
                NotebookId = seeded.NotebookId,
                Active = true,
                MaxUserMessageLength = 5
            };
            db.PublishedGuides.Add(publishedGuide);
            await db.SaveChangesAsync();
            publishedGuideId = publishedGuide.Id;
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(verifyScope);
        var request = new SendMessageRequest { Instructions = "This message is far too long", ModelDeploymentId = ModelId };

        var act = async () => await CollectAsync(
            svc.SendMessageStreamAsync(seeded.ConversationId, request, publisherId: publishedGuideId.ToString(), externalUserIdentity: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exceeds maximum length*");
    }

    [TestMethod]
    public async Task SendMessageStream_Enforces_MaxTurns_from_published_guide()
    {
        SeededConversation seeded;
        Guid publishedGuideId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seeded = await SeedConversationAsync(db);

            // Seed an existing turn so the conversation already has 1 turn.
            db.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = seeded.ConversationId,
                TurnIndex = 1,
                AssistantName = "assistant",
                Instructions = "prior",
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Status = "completed"
            });

            var publishedGuide = new PublishedGuide
            {
                GuideId = seeded.GuideId,
                NotebookId = seeded.NotebookId,
                Active = true,
                MaxTurns = 1
            };
            db.PublishedGuides.Add(publishedGuide);
            await db.SaveChangesAsync();
            publishedGuideId = publishedGuide.Id;
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(verifyScope);
        var request = new SendMessageRequest { Instructions = "Another turn", ModelDeploymentId = ModelId };

        var act = async () => await CollectAsync(
            svc.SendMessageStreamAsync(seeded.ConversationId, request, publisherId: publishedGuideId.ToString(), externalUserIdentity: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum of 1 turns*");
    }

    [TestMethod]
    public async Task ResumeAfterExternalToolResultsStream_Continues_run_and_persists_new_assistant_message()
    {
        SeededConversation seeded;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seeded = await SeedConversationAsync(db);

            db.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = seeded.ConversationId,
                TurnIndex = 1,
                AssistantName = "assistant",
                ModelDeploymentId = ModelId,
                Instructions = "previous question",
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow,
                Status = "streaming"
            });

            db.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage
                {
                    NotebookConversationId = seeded.ConversationId,
                    TurnIndex = 1,
                    MessageSequence = 1,
                    Role = DataModelChatRole.User,
                    Content = "previous question",
                    Created = DateTime.UtcNow
                },
                new NotebookConversationMessage
                {
                    NotebookConversationId = seeded.ConversationId,
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "prior assistant reply",
                    Created = DateTime.UtcNow.AddSeconds(1)
                });
            await db.SaveChangesAsync();
        }

        List<StreamingEvent> events;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var svc = ResolveService(scope);
            events = await CollectAsync(svc.ResumeAfterExternalToolResultsStreamAsync(
                seeded.ConversationId, publisherId: null, externalUserIdentity: null));
        }

        events.Select(e => e.EventType).Should().Contain(StreamingEventTypes.Complete);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // A new assistant message produced by the continued run is appended to the existing turn.
        var assistantMessages = await verifyDb.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == seeded.ConversationId
                        && m.Role == DataModelChatRole.Assistant
                        && m.TurnIndex == 1)
            .ToListAsync();
        assistantMessages.Should().Contain(m => m.Content == FakeAssistantText);

        var turn = await verifyDb.ConversationTurns
            .SingleAsync(t => t.NotebookConversationId == seeded.ConversationId && t.TurnIndex == 1);
        turn.Status.Should().Be("completed");
    }
}
