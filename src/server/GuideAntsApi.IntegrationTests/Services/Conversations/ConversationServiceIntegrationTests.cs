using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Real-SQL integration coverage for <see cref="ConversationService"/>.
/// These exercise the relational/SQL-only paths (READ UNCOMMITTED hints, UsageEvent
/// subqueries, FK-driven lock checks, GroupBy projections) that EF-InMemory cannot run.
/// </summary>
[TestClass]
public sealed class ConversationServiceIntegrationTests : BaseEndpointTest
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task WaitForTurnStatusAsync(
        ApplicationDbContext db,
        Guid conversationId,
        string expectedStatus,
        TimeSpan? timeout = null)
    {
        var waitTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = await db.ConversationTurns
                .AsNoTracking()
                .Where(t => t.NotebookConversationId == conversationId)
                .Select(t => t.Status)
                .SingleAsync();
            if (string.Equals(status, expectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(100);
        }

        var finalStatus = await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId)
            .Select(t => t.Status)
            .SingleAsync();
        finalStatus.Should().Be(expectedStatus);
    }

    private static async Task WaitForLockReleasedAsync(
        ApplicationDbContext db,
        Guid conversationId,
        TimeSpan? timeout = null)
    {
        var waitTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!await db.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId))
            {
                return;
            }

            await Task.Delay(100);
        }

        (await db.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId)).Should().BeFalse();
    }

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

    private static IConversationService ResolveService(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IConversationService>();

    private static async Task<(Guid projectId, Guid notebookId)> SeedProjectNotebookAsync(
        ApplicationDbContext db,
        bool projectDeleted = false)
    {
        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive)
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"Conv Svc Project {Guid.NewGuid():N}",
            Slug = $"conv-{Guid.NewGuid():N}",
            Description = "integration",
            Deleted = projectDeleted,
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"Conv Svc Notebook {Guid.NewGuid():N}",
            Slug = $"nb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        return (project.Id, notebook.Id);
    }

    private static async Task<Guid> SeedConversationAsync(ApplicationDbContext db, Guid notebookId, string title = "Conversation")
    {
        var conv = new NotebookConversation { NotebookId = notebookId, Title = title };
        db.NotebookConversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    // A composite FK requires every message to reference an existing ConversationTurn
    // (NotebookConversationId, TurnIndex). Seed the parent turn for any turn index a test uses.
    private static void AddTurn(ApplicationDbContext db, Guid conversationId, int turnIndex, string assistantName = "assistant")
    {
        db.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = conversationId,
            TurnIndex = turnIndex,
            AssistantName = assistantName,
            Instructions = "seed",
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        });
    }

    private static string SerializeToolCalls(string callId, string functionName)
    {
        var toolCalls = new List<ChatToolCall>
        {
            new()
            {
                Id = callId,
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = functionName,
                    Arguments = JsonSerializer.SerializeToElement(new { query = "value" })
                }
            }
        };
        return JsonSerializer.Serialize(toolCalls, CamelCase);
    }

    private static async Task EnsureUserAsync(ApplicationDbContext db, Guid userId, string email, string name)
    {
        if (await db.Users.AnyAsync(u => u.Id == userId))
        {
            return;
        }

        db.Users.Add(new User
        {
            Id = userId,
            Name = name,
            Email = email,
            PasswordHash = "integration-test-hash",
            SecurityStamp = Guid.NewGuid(),
            LastLoginAt = DateTime.UtcNow,
            ApprovedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<(string EventType, string Payload)>> SendConversationStreamToCompletionAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var events = new List<(string EventType, string Payload)>();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
            {
                var payload = line["data:".Length..].Trim();
                events.Add((currentEvent, payload));

                if (currentEvent == StreamingEventTypes.Error)
                {
                    Assert.Fail($"Stream returned error: {payload}");
                }
            }
        }

        events.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);
        return events;
    }

    private async Task<List<(string EventType, string Payload)>> SendConversationStreamCollectAllAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var events = new List<(string EventType, string Payload)>();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
            {
                events.Add((currentEvent, line["data:".Length..].Trim()));
            }
        }

        return events;
    }

    private async Task<List<(string EventType, string Payload)>> SendConversationStreamUntilCancelledAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var events = new List<(string EventType, string Payload)>();

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 499)
        {
            resp.EnsureSuccessStatusCode();
        }

        if (!resp.IsSuccessStatusCode)
        {
            return events;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.EndOfStream)
                {
                    break;
                }

                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line["event:".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
                {
                    events.Add((currentEvent, line["data:".Length..].Trim()));
                }
            }
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
            // The test host aborts the response body when the client cancels mid-stream.
        }

        return events;
    }

    private static async Task<Guid> GetActiveTurnIdAsync(ApplicationDbContext db, Guid conversationId) =>
        (await db.ConversationTurns
            .AsNoTracking()
            .Where(t => t.NotebookConversationId == conversationId)
            .SingleAsync()).Id;

    private async Task RequestTurnCancelAsync(Guid turnId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = SharedFactory!.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ConversationStreamRunRegistry>();
            if (registry.RequestCancel(turnId))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Turn {turnId} was not active for cancellation");
    }

    private async Task CancelTurnViaApiAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        Guid turnId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = SharedFactory!.Services.CreateScope())
            {
                var registry = scope.ServiceProvider.GetRequiredService<ConversationStreamRunRegistry>();
                if (!registry.IsActive(turnId))
                {
                    await Task.Delay(50);
                    continue;
                }
            }

            var resp = await Client.PostAsync(
                $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/turns/{turnId}/cancel",
                null);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                await Task.Delay(50);
                continue;
            }

            resp.EnsureSuccessStatusCode();
            return;
        }

        Assert.Fail($"Turn {turnId} was not active for cancellation");
    }

    private async Task<HttpStatusCode> SendConversationStreamStatusAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Client.SendAsync(req);
        return resp.StatusCode;
    }

    private async Task<List<(string EventType, string Payload)>> ObserveConversationEventsCollectAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/events");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var events = new List<(string EventType, string Payload)>();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
            {
                events.Add((currentEvent, line["data:".Length..].Trim()));
            }
        }

        return events;
    }

    private async Task<List<StreamingEvent>> CollectServiceStreamAsync(
        IConversationService service,
        Guid conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var events = new List<StreamingEvent>();
        await foreach (var ev in service.SendMessageStreamToConversationAsync(conversationId, request, cancellationToken))
        {
            events.Add(ev);
        }

        return events;
    }

    private static int IndexOfEvent(IReadOnlyList<(string EventType, string Payload)> events, string eventType) =>
        events.ToList().FindIndex(e => e.EventType == eventType);

    // ----- GetConversationByIdAsync -----

    [TestMethod]
    public async Task GetConversationById_ReturnsNull_WhenMissing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var result = await svc.GetConversationByIdAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetConversationById_Projects_messages_with_edit_history_user_and_attachments()
    {
        Guid conversationId;
        Guid userId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            AddTurn(db, conversationId, 1);

            var user = new User
            {
                Id = Guid.NewGuid(),
                Name = "Editor Person",
                Email = "editor.person@example.com",
                PasswordHash = "x"
            };
            db.Users.Add(user);
            userId = user.Id;

            var userMessage = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "Question from user",
                UserId = userId,
                Created = DateTime.UtcNow
            };
            var assistantMessage = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "Edited answer",
                IsEdited = true,
                LastEditedByUserId = userId,
                LastEditedAt = DateTime.UtcNow,
                Created = DateTime.UtcNow.AddSeconds(1)
            };
            // A streaming row must be excluded.
            var streamingMessage = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 3,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "still streaming",
                IsStreaming = true,
                Created = DateTime.UtcNow.AddSeconds(2)
            };
            db.NotebookConversationMessages.AddRange(userMessage, assistantMessage, streamingMessage);
            await db.SaveChangesAsync();

            db.MessageEditHistories.Add(new MessageEditHistory
            {
                Id = Guid.NewGuid(),
                MessageId = assistantMessage.Id,
                OriginalContent = "Original answer",
                FirstEditedByUserId = userId,
                FirstEditedAt = DateTime.UtcNow
            });

            var file = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = "Output/result.png",
                FileSize = 256,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash-img"
            };
            file.GenerateDocumentId(notebookId);
            db.NotebookFiles.Add(file);
            await db.SaveChangesAsync();

            db.MessageAttachments.Add(new MessageAttachment
            {
                MessageId = assistantMessage.Id,
                NotebookFileId = file.Id,
                Type = AttachmentType.Created,
                OrderIndex = 0,
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);
        var dto = await service.GetConversationByIdAsync(conversationId);

        dto.Should().NotBeNull();
        dto!.Messages.Should().HaveCount(2); // streaming row filtered out
        var assistant = dto.Messages.Should().ContainSingle(m => m.Role == DataModelChatRole.Assistant).Subject;
        assistant.IsEdited.Should().BeTrue();
        assistant.OriginalContent.Should().Be("Original answer");
        assistant.UserName.Should().Be("Editor Person");
        assistant.UserEmail.Should().Be("editor.person@example.com");
        assistant.Attachments.Should().ContainSingle(a => a.FileName == "result.png" && a.FileType == "image");
    }

    // ----- GetConversationWithMessagesAsync (READ UNCOMMITTED + projection) -----

    [TestMethod]
    public async Task GetConversationWithMessages_ReturnsNull_WhenMissing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var result = await svc.GetConversationWithMessagesAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetConversationWithMessages_Projects_turn_files_thinking_tool_calls_and_filters_duplicates()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Projection test");

            db.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                AssistantName = "assistant",
                Instructions = "do work",
                FilesCreated = JsonSerializer.Serialize(new List<string> { "Output/new.txt" }, CamelCase),
                FilesModified = JsonSerializer.Serialize(new List<string> { "Output/mod.txt" }, CamelCase),
                Created = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            });

            var toolCallsJson = SerializeToolCalls("call_1", "SearchDocs");
            var thinkingJson = JsonSerializer.Serialize(
                new List<ChatThinkingBlock> { ChatThinkingBlock.ForThinking("reasoning step", "sig") },
                CamelCase);

            db.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 1,
                    Role = DataModelChatRole.User,
                    Content = "ask",
                    Created = DateTime.UtcNow
                },
                // duplicate assistant WITHOUT tool calls (should be filtered out)
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "Same answer",
                    Created = DateTime.UtcNow.AddSeconds(1)
                },
                // assistant WITH tool calls + thinking blocks (kept, is last assistant of turn)
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 3,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "Same answer",
                    ToolCalls = toolCallsJson,
                    ThinkingBlocksJson = thinkingJson,
                    Created = DateTime.UtcNow.AddSeconds(2)
                },
                // streaming row excluded
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 4,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "streaming",
                    IsStreaming = true,
                    Created = DateTime.UtcNow.AddSeconds(3)
                });
            await db.SaveChangesAsync();
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);
        var dto = await service.GetConversationWithMessagesAsync(conversationId);

        dto.Should().NotBeNull();
        dto!.AssistantName.Should().Be("assistant");

        var assistantMessages = dto.Messages.Where(m => m.Role == DataModelChatRole.Assistant).ToList();
        // duplicate (no tool calls) filtered; remaining assistant content message has tool calls
        var withToolCalls = assistantMessages.Should().ContainSingle(m => m.ToolCalls != null && m.ToolCalls.Count > 0).Subject;
        withToolCalls.ToolCalls!.Should().ContainSingle(c => c.Function.Name == "SearchDocs");
        withToolCalls.TurnFilesCreated.Should().Contain("Output/new.txt");
        withToolCalls.TurnFilesModified.Should().Contain("Output/mod.txt");

        // thinking block surfaced as its own synthetic assistant message
        dto.Messages.Should().Contain(m => m.Content == "reasoning step");
        // streaming row excluded
        dto.Messages.Should().NotContain(m => m.Content == "streaming");
    }

    // ----- GetListAsync (READ UNCOMMITTED + UsageEvent LastActivity subquery) -----

    [TestMethod]
    public async Task GetList_ReturnsEmpty_WhenNotebookMissing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var list = await svc.GetListAsync(Guid.NewGuid());

        list.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetList_Orders_by_usage_event_last_activity()
    {
        Guid notebookId;
        Guid projectId;
        Guid older;
        Guid newer;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);

            var c1 = new NotebookConversation { NotebookId = notebookId, Title = "Older activity", Created = DateTime.UtcNow.AddHours(-5) };
            var c2 = new NotebookConversation { NotebookId = notebookId, Title = "Newer activity", Created = DateTime.UtcNow.AddHours(-4) };
            db.NotebookConversations.AddRange(c1, c2);
            await db.SaveChangesAsync();
            older = c1.Id;
            newer = c2.Id;

            db.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    NotebookId = notebookId,
                    ConversationId = older,
                    Category = UsageCategory.ChatCompletion,
                    Created = DateTime.UtcNow.AddHours(-3)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    NotebookId = notebookId,
                    ConversationId = newer,
                    Category = UsageCategory.ChatCompletion,
                    Created = DateTime.UtcNow.AddMinutes(-1)
                });
            await db.SaveChangesAsync();
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);
        var list = await service.GetListAsync(notebookId);

        list.Should().HaveCount(2);
        list[0].Id.Should().Be(newer);
        list[1].Id.Should().Be(older);
        list[0].LastActivity.Should().BeOnOrAfter(list[1].LastActivity);
    }

    // ----- CreateConversationAsync -----

    [TestMethod]
    public async Task CreateConversation_Throws_when_notebook_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var act = () => svc.CreateConversationAsync(Guid.NewGuid(), "Title");

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Notebook not found*");
    }

    [TestMethod]
    public async Task CreateConversation_Trims_title_and_defaults_untitled()
    {
        Guid notebookId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (_, notebookId) = await SeedProjectNotebookAsync(db);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);

        var trimmed = await service.CreateConversationAsync(notebookId, "  Trimmed  ");
        trimmed.Title.Should().Be("Trimmed");

        var untitled = await service.CreateConversationAsync(notebookId, "   ");
        untitled.Title.Should().Be("Untitled");
    }

    // ----- RenameConversationAsync -----

    [TestMethod]
    public async Task RenameConversation_Throws_when_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var act = () => svc.RenameConversationAsync(Guid.NewGuid(), "New");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [TestMethod]
    public async Task RenameConversation_Updates_and_keeps_existing_when_blank()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Initial");
        }

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var service = ResolveService(scope);
            await service.RenameConversationAsync(conversationId, "  Renamed  ");
            await service.RenameConversationAsync(conversationId, "   ");
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var title = await db2.NotebookConversations.Where(c => c.Id == conversationId).Select(c => c.Title).FirstAsync();
        title.Should().Be("Renamed");
    }

    // ----- DeleteConversationAsync -----

    [TestMethod]
    public async Task DeleteConversation_NoOp_when_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var act = () => svc.DeleteConversationAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task DeleteConversation_Removes_conversation_and_messages()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            AddTurn(db, conversationId, 1);
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "hi",
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            await ResolveService(scope).DeleteConversationAsync(conversationId);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.NotebookConversations.AnyAsync(c => c.Id == conversationId)).Should().BeFalse();
        (await db2.NotebookConversationMessages.AnyAsync(m => m.NotebookConversationId == conversationId)).Should().BeFalse();
    }

    // ----- SendMessageStreamToConversationAsync (real service through SSE endpoint) -----

    [TestMethod]
    public async Task SendMessageStream_Persists_turn_messages_usage_and_releases_lock()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Streaming persistence");
        }

        var events = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Hello from the persistence test", assistantName = "assistant" });

        events.Should().Contain(e => e.EventType == StreamingEventTypes.AssistantMessage);
        events.Should().Contain(e => e.EventType == StreamingEventTypes.Usage);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var turn = await db2.ConversationTurns.SingleAsync(t => t.NotebookConversationId == conversationId);
        turn.TurnIndex.Should().Be(1);
        turn.AssistantName.Should().Be("assistant");
        turn.Status.Should().Be("completed");
        turn.ChatRunOutputJson.Should().NotBeNullOrWhiteSpace();
        turn.UsageJson.Should().NotBeNullOrWhiteSpace();

        var messages = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId)
            .OrderBy(m => m.MessageSequence)
            .ToListAsync();

        messages.Should().ContainSingle(m =>
            m.Role == DataModelChatRole.User &&
            m.Content == "Hello from the persistence test" &&
            m.UserId != null);

        var assistant = messages.Should().ContainSingle(m => m.Role == DataModelChatRole.Assistant).Subject;
        assistant.Content.Should().Contain("Test assistant response.");
        assistant.IsStreaming.Should().BeFalse();
        assistant.AssistantName.Should().Be("assistant");
        assistant.AssistantId.Should().NotBeNull();

        var usage = await db2.UsageEvents
            .Where(u => u.ConversationId == conversationId && u.Category == UsageCategory.ChatCompletion)
            .SingleAsync();
        usage.NotebookConversationMessageId.Should().Be(assistant.Id);
        usage.ValueInput.Should().Be(1);
        usage.ValueOutput.Should().Be(1);

        (await db2.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId)).Should().BeFalse();
    }

    [TestMethod]
    public async Task SendMessageStream_Cancel_finalizes_partial_message_marks_turn_cancelled_and_prunes_incomplete_tool_calls()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Cancel partial stream");
        }

        FakeChatCompletionBehavior.Instance.Reset();
        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.SlowCancellableStream;
        FakeChatCompletionBehavior.Instance.ChunkDelayMs = 120;

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            await SendConversationStreamUntilCancelledAsync(
                projectId,
                notebookId,
                conversationId,
                new { instructions = "Cancel me mid-stream", assistantName = "assistant" },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected when the HTTP stream is aborted
        }

        Guid turnId;
        using (var midScope = SharedFactory!.Services.CreateScope())
        {
            var midDb = midScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registry = midScope.ServiceProvider.GetRequiredService<ConversationStreamRunRegistry>();
            turnId = await GetActiveTurnIdAsync(midDb, conversationId);

            (await midDb.ConversationTurns
                .AsNoTracking()
                .Where(t => t.Id == turnId)
                .Select(t => t.Status)
                .SingleAsync()).Should().Be("streaming");
            (await midDb.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId)).Should().BeTrue();
            registry.IsActive(turnId).Should().BeTrue();
        }

        await RequestTurnCancelAsync(turnId);

        using (var verifyScope = SharedFactory!.Services.CreateScope())
        {
            var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await WaitForTurnStatusAsync(db2, conversationId, "cancelled");
            await WaitForLockReleasedAsync(db2, conversationId);

            var turn = await db2.ConversationTurns.SingleAsync(t => t.NotebookConversationId == conversationId);

            var assistant = await db2.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant)
                .SingleAsync();
            assistant.IsStreaming.Should().BeFalse();
            assistant.Content.Should().NotBeNullOrWhiteSpace();
            assistant.Content.Should().Contain("Partial");

            turn.ChatRunOutputJson.Should().NotBeNullOrWhiteSpace();

            (await db2.UsageEvents.AnyAsync(u =>
                    u.ConversationId == conversationId
                    && u.Category == UsageCategory.ChatCompletion
                    && u.ValueInput == 0
                    && u.ValueOutput == 0))
                .Should().BeFalse();

            (await db2.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId)).Should().BeFalse();
        }

        Guid pruneConversationId;
        using (var seedScope = SharedFactory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            pruneConversationId = await SeedConversationAsync(seedDb, notebookId, "Cancel prune tools");
        }

        using var pruneCts = new CancellationTokenSource();
        FakeChatCompletionBehavior.Instance.Reset();
        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.ToolCallsCancelBeforeExecution;
        Guid pruneTurnId = Guid.Empty;
        FakeChatCompletionBehavior.Instance.OnToolCallsReturning = () => pruneCts.Cancel();

        try
        {
            await SendConversationStreamUntilCancelledAsync(
                projectId,
                notebookId,
                pruneConversationId,
                new { instructions = "Trigger tool calls then cancel", assistantName = "assistant" },
                pruneCts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected when the HTTP stream is aborted
        }

        using (var pruneMidScope = SharedFactory!.Services.CreateScope())
        {
            var pruneMidDb = pruneMidScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            pruneTurnId = await GetActiveTurnIdAsync(pruneMidDb, pruneConversationId);
            (await pruneMidDb.ConversationTurns
                .AsNoTracking()
                .Where(t => t.Id == pruneTurnId)
                .Select(t => t.Status)
                .SingleAsync()).Should().Be("streaming");
        }

        await CancelTurnViaApiAsync(projectId, notebookId, pruneConversationId, pruneTurnId);

        using (var pruneVerifyScope = SharedFactory!.Services.CreateScope())
        {
            var pruneVerifyDb = pruneVerifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await WaitForTurnStatusAsync(pruneVerifyDb, pruneConversationId, "cancelled");

            var pruneTurn = await pruneVerifyDb.ConversationTurns.SingleAsync(t => t.NotebookConversationId == pruneConversationId);

            var pruneAssistant = await pruneVerifyDb.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == pruneConversationId && m.Role == DataModelChatRole.Assistant)
                .SingleAsync();
            pruneAssistant.ToolCalls.Should().BeNull();

            (await pruneVerifyDb.NotebookConversationMessages
                .AnyAsync(m => m.NotebookConversationId == pruneConversationId && m.Role == DataModelChatRole.Tool))
                .Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task SendMessageStream_TimeoutDuringStream_preserves_partial_text_and_marks_turn_timed_out()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Timeout partial stream");
        }

        FakeChatCompletionBehavior.Instance.Reset();
        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.PartialTimeoutStream;
        FakeChatCompletionBehavior.Instance.SlowStreamText = "Partial timeout assistant text.";

        var events = await SendConversationStreamCollectAllAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Timeout during stream", assistantName = "assistant" });

        events.Should().Contain(e => e.EventType == StreamingEventTypes.Error);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await WaitForTurnStatusAsync(db2, conversationId, "timed_out");

        var turn = await db2.ConversationTurns.SingleAsync(t => t.NotebookConversationId == conversationId);
        turn.TerminationCode.Should().Be("local_llm_timeout");
        turn.ChatRunOutputJson.Should().NotBeNullOrWhiteSpace();

        var assistant = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant)
            .SingleAsync();
        assistant.IsStreaming.Should().BeFalse();
        assistant.Content.Should().Contain("Partial timeout");

        (await db2.UsageEvents.AnyAsync(u =>
                u.ConversationId == conversationId
                && u.Category == UsageCategory.ChatCompletion
                && u.ValueInput == 0
                && u.ValueOutput == 0))
            .Should().BeFalse();

        (await db2.ConversationLocks.AnyAsync(l => l.ConversationId == conversationId)).Should().BeFalse();

        FakeChatCompletionBehavior.Instance.Reset();
    }

    [TestMethod]
    public async Task SendMessageStream_Cancel_during_thinking_persists_thinking_without_zero_token_usage()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Cancel during thinking");
        }

        FakeChatCompletionBehavior.Instance.Reset();
        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.ThinkingStream;
        FakeChatCompletionBehavior.Instance.ThinkingText = "searching for the hosted file name in the tool result";
        FakeChatCompletionBehavior.Instance.FinalAssistantText = "should not be persisted";

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(120));
        try
        {
            await SendConversationStreamUntilCancelledAsync(
                projectId,
                notebookId,
                conversationId,
                new { instructions = "Think then answer", assistantName = "assistant" },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected when the HTTP stream is aborted
        }

        Guid turnId;
        using (var midScope = SharedFactory!.Services.CreateScope())
        {
            var midDb = midScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            turnId = await GetActiveTurnIdAsync(midDb, conversationId);
        }

        await RequestTurnCancelAsync(turnId);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await WaitForTurnStatusAsync(db2, conversationId, "cancelled");
        await WaitForLockReleasedAsync(db2, conversationId);

        var turn = await db2.ConversationTurns.SingleAsync(t => t.NotebookConversationId == conversationId);
        turn.ChatRunOutputJson.Should().NotBeNullOrWhiteSpace();

        var assistant = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant)
            .SingleAsync();
        assistant.IsStreaming.Should().BeFalse();
        assistant.ThinkingBlocksJson.Should().NotBeNullOrWhiteSpace();
        assistant.Content.Should().NotBe("should not be persisted");

        (await db2.UsageEvents.AnyAsync(u =>
                u.ConversationId == conversationId
                && u.Category == UsageCategory.ChatCompletion
                && u.ValueInput == 0
                && u.ValueOutput == 0))
            .Should().BeFalse();

        var service = ResolveService(verifyScope);
        var dto = await service.GetConversationWithMessagesAsync(conversationId);
        dto!.Messages.Should().NotContain(m =>
            m.Role == DataModelChatRole.Assistant && string.IsNullOrWhiteSpace(m.Content));
        dto.Messages.Should().Contain(m =>
            m.Role == DataModelChatRole.Assistant && m.Content.Contains("searching", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SendMessageStream_Persists_tool_call_assistant_and_tool_result_with_idempotent_tool_usage()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Tool call persistence");
        }

        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.ToolCallThenReply;
        FakeChatCompletionBehavior.Instance.FinalAssistantText = "Tool flow complete.";

        var events = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Use a tool", assistantName = "assistant" });

        events.Should().Contain(e => e.EventType == StreamingEventTypes.ToolResult);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var assistantWithToolCalls = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId
                        && m.Role == DataModelChatRole.Assistant
                        && m.ToolCalls != null)
            .SingleAsync();
        assistantWithToolCalls.ToolCalls.Should().Contain(FakeChatCompletionBehavior.Instance.ToolCallId);

        var toolMessage = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Tool)
            .SingleAsync();
        toolMessage.ToolCallId.Should().Be(FakeChatCompletionBehavior.Instance.ToolCallId);
        toolMessage.FunctionName.Should().Be(FakeChatCompletionBehavior.Instance.ToolFunctionName);
        toolMessage.Content.Should().NotBeNullOrWhiteSpace();

        var toolUsageEvents = await db2.UsageEvents
            .Where(u => u.NotebookConversationMessageId == toolMessage.Id && u.Category == UsageCategory.ToolCall)
            .ToListAsync();
        toolUsageEvents.Should().ContainSingle();
    }

    [TestMethod]
    public async Task SendMessageStream_With_attachment_persists_MessageAttachment_and_includes_file_in_history()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        Guid notebookFileId;
        const string fileContent = "attachment body for history projection";
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Attachment send");

            var fileService = scope.ServiceProvider.GetRequiredService<INotebookFileService>();
            var created = await fileService.CreateTextFileAsync(projectId, notebookId, "Output/attach-notes.txt", fileContent);
            created.Should().NotBeNull();
            notebookFileId = created.Id;
        }

        var events = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new
            {
                instructions = " ",
                assistantName = "assistant",
                attachments = new[] { new { notebookFileId, uploadType = 0 } }
            });

        events.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userMessage = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.User)
            .SingleAsync();

        var attachmentRows = await db2.MessageAttachments
            .Where(ma => ma.MessageId == userMessage.Id)
            .ToListAsync();
        attachmentRows.Should().ContainSingle(a => a.NotebookFileId == notebookFileId);

        var service = ResolveService(verifyScope);
        var historyBuilder = verifyScope.ServiceProvider.GetRequiredService<IConversationHistoryBuilder>();
        var conv = await db2.NotebookConversations
            .Include(c => c.Messages)
            .Include(c => c.Turns)
            .Include(c => c.Notebook)
            .FirstAsync(c => c.Id == conversationId);

        var history = await historyBuilder.BuildOpenAiMessagesAsync(conv, "assistant");
        history.Should().Contain(m => m.GetText().Contains("attach-notes.txt"));
        history.Should().Contain(m => m.GetText().Contains(fileContent));

        var dto = await service.GetConversationByIdAsync(conversationId);
        dto.Should().NotBeNull();
        dto!.Messages.Should().ContainSingle(m =>
            m.Attachments != null && m.Attachments.Any(a => a.FileName == "attach-notes.txt"));
    }

    [TestMethod]
    public async Task SendMessageStream_With_multiple_attachments_persists_distinct_order_indexes()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        Guid firstNotebookFileId;
        Guid secondNotebookFileId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Multiple attachments send");

            var fileService = scope.ServiceProvider.GetRequiredService<INotebookFileService>();
            var first = await fileService.CreateTextFileAsync(projectId, notebookId, "Output/first-attach.txt", "first body");
            var second = await fileService.CreateTextFileAsync(projectId, notebookId, "Output/second-attach.txt", "second body");
            first.Should().NotBeNull();
            second.Should().NotBeNull();
            firstNotebookFileId = first!.Id;
            secondNotebookFileId = second!.Id;
        }

        var events = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new
            {
                instructions = " ",
                assistantName = "assistant",
                attachments = new[]
                {
                    new { notebookFileId = firstNotebookFileId, uploadType = 0 },
                    new { notebookFileId = secondNotebookFileId, uploadType = 0 }
                }
            });

        events.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userMessage = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.User)
            .SingleAsync();

        var attachmentRows = await db2.MessageAttachments
            .Where(ma => ma.MessageId == userMessage.Id)
            .OrderBy(ma => ma.OrderIndex)
            .ToListAsync();

        attachmentRows.Should().HaveCount(2);
        attachmentRows[0].NotebookFileId.Should().Be(firstNotebookFileId);
        attachmentRows[0].OrderIndex.Should().Be(0);
        attachmentRows[1].NotebookFileId.Should().Be(secondNotebookFileId);
        attachmentRows[1].OrderIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task SendMessageStream_Emits_thinking_blocks_in_stream_and_persists_to_assistant_message()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Thinking stream");
        }

        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.ThinkingStream;
        FakeChatCompletionBehavior.Instance.ThinkingText = "integration reasoning step";
        FakeChatCompletionBehavior.Instance.FinalAssistantText = "Answer after thinking.";

        var events = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Think then answer", assistantName = "assistant" });

        events.Should().Contain(e =>
            e.EventType == StreamingEventTypes.AssistantMessage &&
            e.Payload.Contains("contentDelta", StringComparison.Ordinal) &&
            e.Payload.Contains("integration", StringComparison.Ordinal));

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var assistant = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant)
            .SingleAsync();
        assistant.ThinkingBlocksJson.Should().NotBeNullOrWhiteSpace();
        assistant.ThinkingBlocksJson.Should().Contain("integration reasoning step");

        var service = ResolveService(verifyScope);
        var dto = await service.GetConversationWithMessagesAsync(conversationId);
        dto!.Messages.Should().Contain(m => m.Content == "integration reasoning step");
    }

    [TestMethod]
    public async Task SendMessageStream_Checkpoints_thinking_before_terminalization()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Thinking heartbeat");
        }

        FakeChatCompletionBehavior.Instance.Reset();
        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.LongThinkingStream;
        FakeChatCompletionBehavior.Instance.ChunkDelayMs = 40;
        FakeChatCompletionBehavior.Instance.ThinkingText = string.Join(
            ' ',
            Enumerable.Range(1, 25).Select(i => $"word{i}"));
        FakeChatCompletionBehavior.Instance.FinalAssistantText = "should not be reached before cancel";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var streamTask = Task.Run(async () =>
        {
            try
            {
                await SendConversationStreamUntilCancelledAsync(
                    projectId,
                    notebookId,
                    conversationId,
                    new { instructions = "Long thinking heartbeat", assistantName = "assistant" },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected when the HTTP stream is aborted
            }
        });

        var sawHeartbeat = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var pollScope = SharedFactory!.Services.CreateScope();
            var db = pollScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var turn = await db.ConversationTurns
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.NotebookConversationId == conversationId);
            if (turn is { Status: "streaming", CheckpointVersion: > 0 })
            {
                turn.LastUpdated.Should().BeAfter(turn.Created);

                var checkpointedAssistant = await db.NotebookConversationMessages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m =>
                        m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant);
                checkpointedAssistant.Should().NotBeNull();
                checkpointedAssistant!.ThinkingBlocksJson.Should().NotBeNullOrWhiteSpace();
                checkpointedAssistant.ThinkingBlocksJson.Should().Contain("word");

                sawHeartbeat = true;
                break;
            }

            await Task.Delay(100);
        }

        sawHeartbeat.Should().BeTrue("thinking-only stream should checkpoint before terminalization");

        Guid turnId;
        using (var midScope = SharedFactory!.Services.CreateScope())
        {
            var midDb = midScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            turnId = await GetActiveTurnIdAsync(midDb, conversationId);
        }

        await cts.CancelAsync();
        await RequestTurnCancelAsync(turnId);
        await streamTask;

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await WaitForTurnStatusAsync(db2, conversationId, "cancelled");

        var assistant = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant)
            .SingleAsync();
        assistant.ThinkingBlocksJson.Should().NotBeNullOrWhiteSpace();
        assistant.ThinkingBlocksJson.Should().Contain("word1");
        assistant.Content.Should().NotBe("should not be reached before cancel");
    }

    [TestMethod]
    public async Task SendMessageStream_SseDisconnect_keeps_worker_locked_until_explicit_cancel_and_reattach_observe()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "SSE disconnect reattach");
        }

        FakeChatCompletionBehavior.Instance.Reset();
        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.SlowCancellableStream;
        FakeChatCompletionBehavior.Instance.ChunkDelayMs = 400;
        FakeChatCompletionBehavior.Instance.SlowStreamText = string.Join(
            ' ',
            Enumerable.Range(1, 80).Select(i => $"word{i}"));

        using var sendCts = new CancellationTokenSource();
        sendCts.CancelAfter(TimeSpan.FromMilliseconds(400));

        var sendTask = Task.Run(async () =>
        {
            try
            {
                await SendConversationStreamUntilCancelledAsync(
                    projectId,
                    notebookId,
                    conversationId,
                    new { instructions = "Disconnect then observe", assistantName = "assistant" },
                    sendCts.Token);
            }
            catch (OperationCanceledException)
            {
                // expected when the primary SSE client disconnects
            }
        });

        Guid turnId = Guid.Empty;
        var sawPartial = false;
        var partialDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < partialDeadline)
        {
            using var pollScope = SharedFactory!.Services.CreateScope();
            var pollDb = pollScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assistant = await pollDb.NotebookConversationMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(m =>
                    m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant);
            if (assistant is { Content: { Length: > 0 } })
            {
                sawPartial = true;
                turnId = await GetActiveTurnIdAsync(pollDb, conversationId);
                break;
            }

            await Task.Delay(50);
        }

        sawPartial.Should().BeTrue("worker should persist partial content while SSE is active");

        var streamingConfirmed = false;
        var streamingDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < streamingDeadline)
        {
            using var pollScope = SharedFactory!.Services.CreateScope();
            var pollDb = pollScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registry = pollScope.ServiceProvider.GetRequiredService<ConversationStreamRunRegistry>();
            var status = await pollDb.ConversationTurns
                .AsNoTracking()
                .Where(t => t.Id == turnId)
                .Select(t => t.Status)
                .SingleAsync();
            if (status == "streaming" && registry.IsActive(turnId))
            {
                streamingConfirmed = true;
                break;
            }

            await Task.Delay(25);
        }

        streamingConfirmed.Should().BeTrue("turn should remain streaming after SSE disconnect");

        _ = sendTask;

        (await SendConversationStreamStatusAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Should be blocked", assistantName = "assistant" }))
            .Should().Be(HttpStatusCode.Conflict);

        using (var previewScope = SharedFactory!.Services.CreateScope())
        {
            var service = ResolveService(previewScope);
            var dto = await service.GetConversationWithMessagesAsync(conversationId);
            dto!.ActiveTurn.Should().NotBeNull();
            dto.ActiveTurn!.Status.Should().Be("streaming");
            dto.Lock.Should().NotBeNull();
            dto.StreamingPreview.Should().NotBeNull();
            dto.StreamingPreview!.Content.Should().NotBeEmpty();
        }

        using (var undoScope = SharedFactory!.Services.CreateScope())
        {
            var undoSvc = ResolveService(undoScope);
            var act = () => undoSvc.UndoLastForConversationAsync(conversationId);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*locked*");
        }

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var observedEvents = new System.Collections.Concurrent.ConcurrentBag<(string EventType, string Payload)>();
        var observeTask = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/events");
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, observeCts.Token);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(observeCts.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? currentEvent = null;
            while (!observeCts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(observeCts.Token);
                if (line == null)
                {
                    break;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line["event:".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
                {
                    observedEvents.Add((currentEvent, line["data:".Length..].Trim()));
                }
            }
        });

        await RequestTurnCancelAsync(turnId);

        var observeDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < observeDeadline)
        {
            if (observedEvents.Any(e =>
                    e.EventType == StreamingEventTypes.Token
                    || e.EventType == StreamingEventTypes.AssistantMessage
                    || e.EventType == StreamingEventTypes.Complete
                    || e.EventType == StreamingEventTypes.Cancelled))
            {
                break;
            }

            await Task.Delay(50);
        }

        observeCts.Cancel();
        try
        {
            await observeTask;
        }
        catch (OperationCanceledException)
        {
            // expected when ending the observer subscription
        }

        observedEvents.Should().Contain(e =>
            e.EventType == StreamingEventTypes.Token
            || e.EventType == StreamingEventTypes.AssistantMessage
            || e.EventType == StreamingEventTypes.Complete
            || e.EventType == StreamingEventTypes.Cancelled,
            "GET /events should receive live hub events after SSE disconnect");

        using (var verifyScope = SharedFactory!.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registry = verifyScope.ServiceProvider.GetRequiredService<ConversationStreamRunRegistry>();

            await WaitForTurnStatusAsync(verifyDb, conversationId, "cancelled");
            await WaitForLockReleasedAsync(verifyDb, conversationId);
            registry.IsActive(turnId).Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task ObserveEvents_unknown_conversation_returns_404()
    {
        Guid projectId;
        Guid notebookId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
        }

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{Guid.NewGuid()}/events");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task SendMessageStream_Complete_event_is_emitted_after_unlock_for_stream_client_and_observers()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Event ordering");
        }

        using var observerScope = SharedFactory!.Services.CreateScope();
        var hub = observerScope.ServiceProvider.GetRequiredService<IConversationBroadcastHub>();
        using var observerCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var observerEvents = new List<StreamingEvent>();
        var observerTask = Task.Run(async () =>
        {
            await foreach (var ev in hub.SubscribeToConversationAsync(conversationId, $"observer-{Guid.NewGuid():N}", observerCts.Token))
            {
                observerEvents.Add(ev);
            }
        });

        await Task.Delay(100);

        var streamEvents = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Ordering check", assistantName = "assistant" });

        await Task.Delay(200);
        observerCts.Cancel();
        try
        {
            await observerTask;
        }
        catch (OperationCanceledException)
        {
            // expected when the observer subscription is cancelled
        }

        var streamUnlockIndex = IndexOfEvent(streamEvents, StreamingEventTypes.ConversationUnlocked);
        var streamCompleteIndex = IndexOfEvent(streamEvents, StreamingEventTypes.Complete);
        streamCompleteIndex.Should().Be(streamEvents.Count - 1);
        streamUnlockIndex.Should().Be(-1, "unlock is broadcast to observers only, not the active SSE client");

        var observerUnlockIndex = observerEvents.FindIndex(e => e.EventType == StreamingEventTypes.ConversationUnlocked);
        var observerCompleteIndex = observerEvents.FindIndex(e => e.EventType == StreamingEventTypes.Complete);
        observerUnlockIndex.Should().BeGreaterThan(-1);
        observerCompleteIndex.Should().BeGreaterThan(observerUnlockIndex);
    }

    // ----- UndoLastForConversationAsync / UndoForConversationAsync (FK-driven lock) -----

    [TestMethod]
    public async Task UndoLast_Throws_when_conversation_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var act = () => svc.UndoLastForConversationAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [TestMethod]
    public async Task UndoLast_NoOp_when_no_user_messages()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            AddTurn(db, conversationId, 1);
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "assistant only",
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            await ResolveService(scope).UndoLastForConversationAsync(conversationId);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.NotebookConversationMessages.CountAsync(m => m.NotebookConversationId == conversationId)).Should().Be(1);
    }

    [TestMethod]
    public async Task UndoLast_Removes_only_latest_turn()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            SeedTwoTurns(db, conversationId);
            await db.SaveChangesAsync();
        }

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            await ResolveService(scope).UndoLastForConversationAsync(conversationId);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.ConversationTurns.CountAsync(t => t.NotebookConversationId == conversationId)).Should().Be(1);
        var remaining = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId)
            .ToListAsync();
        remaining.Should().OnlyContain(m => m.TurnIndex == 1);
    }

    [TestMethod]
    public async Task UndoForMessage_Throws_when_message_missing()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(verifyScope);

        var act = () => svc.UndoForConversationAsync(conversationId, Guid.NewGuid());

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*Message not found*");
    }

    [TestMethod]
    public async Task UndoForMessage_Removes_from_target_turn_onward()
    {
        Guid conversationId;
        Guid secondTurnUserMessageId = Guid.Empty;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (_, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            var ids = SeedTwoTurns(db, conversationId);
            await db.SaveChangesAsync();
            secondTurnUserMessageId = ids.secondUserMessageId;
        }

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            await ResolveService(scope).UndoForConversationAsync(conversationId, secondTurnUserMessageId);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.ConversationTurns.CountAsync(t => t.NotebookConversationId == conversationId)).Should().Be(1);
        (await db2.NotebookConversationMessages.CountAsync(m => m.NotebookConversationId == conversationId && m.TurnIndex == 2)).Should().Be(0);
    }

    private static (Guid secondUserMessageId, Guid firstUserMessageId) SeedTwoTurns(ApplicationDbContext db, Guid conversationId)
    {
        db.ConversationTurns.AddRange(
            new ConversationTurn
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                AssistantName = "assistant",
                Instructions = "first",
                Created = DateTime.UtcNow.AddMinutes(-2),
                LastUpdated = DateTime.UtcNow.AddMinutes(-2)
            },
            new ConversationTurn
            {
                NotebookConversationId = conversationId,
                TurnIndex = 2,
                AssistantName = "assistant",
                Instructions = "second",
                Created = DateTime.UtcNow.AddMinutes(-1),
                LastUpdated = DateTime.UtcNow.AddMinutes(-1)
            });

        var firstUser = new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            MessageSequence = 1,
            Role = DataModelChatRole.User,
            Content = "first user",
            Created = DateTime.UtcNow.AddMinutes(-2)
        };
        var firstAssistant = new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            MessageSequence = 2,
            Role = DataModelChatRole.Assistant,
            AssistantName = "assistant",
            Content = "first response",
            Created = DateTime.UtcNow.AddMinutes(-2).AddSeconds(1)
        };
        var secondUser = new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 2,
            MessageSequence = 1,
            Role = DataModelChatRole.User,
            Content = "second user",
            Created = DateTime.UtcNow.AddMinutes(-1)
        };
        var secondAssistant = new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 2,
            MessageSequence = 2,
            Role = DataModelChatRole.Assistant,
            AssistantName = "assistant",
            Content = "second response",
            Created = DateTime.UtcNow.AddMinutes(-1).AddSeconds(1)
        };
        db.NotebookConversationMessages.AddRange(firstUser, firstAssistant, secondUser, secondAssistant);
        return (secondUser.Id, firstUser.Id);
    }

    // ----- EditMessageAsync (driven through HTTP endpoint so principal is set) -----

    [TestMethod]
    public async Task EditMessage_endpoint_updates_assistant_message_and_records_history()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        Guid assistantMessageId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            AddTurn(db, conversationId, 1);
            var assistant = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "Original assistant text",
                Created = DateTime.UtcNow
            };
            db.NotebookConversationMessages.Add(assistant);
            await db.SaveChangesAsync();
            assistantMessageId = assistant.Id;
        }

        var editResp = await Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages/{assistantMessageId}",
            new { content = "Edited assistant text" });
        editResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var edited = await db2.NotebookConversationMessages
            .Include(m => m.EditHistory)
            .FirstAsync(m => m.Id == assistantMessageId);
        edited.Content.Should().Be("Edited assistant text");
        edited.IsEdited.Should().BeTrue();
        edited.EditHistory.Should().NotBeNull();
        edited.EditHistory!.OriginalContent.Should().Be("Original assistant text");
    }

    [TestMethod]
    public async Task EditMessage_endpoint_rejects_non_assistant_message()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        Guid userMessageId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
            AddTurn(db, conversationId, 1);
            var userMessage = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "user text",
                Created = DateTime.UtcNow
            };
            db.NotebookConversationMessages.Add(userMessage);
            await db.SaveChangesAsync();
            userMessageId = userMessage.Id;
        }

        var editResp = await Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages/{userMessageId}",
            new { content = "nope" });
        editResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task EditMessage_endpoint_returns_404_for_missing_message()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId);
        }

        var editResp = await Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages/{Guid.NewGuid()}",
            new { content = "x" });
        editResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ----- GetUserConversationsAsync (relational projection, search, sort, paging) -----

    [TestMethod]
    public async Task GetUserConversations_excludes_deleted_projects_and_applies_search_sort_paging()
    {
        var currentUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        SetupAuthentication(userId: currentUserId, email: "owner@example.com", name: "Owner User");

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await EnsureUserAsync(db, currentUserId, "owner@example.com", "Owner User");
            await EnsureUserAsync(db, otherUserId, "other@example.com", "Other User");

            var (_, liveNotebook) = await SeedProjectNotebookAsync(db);
            var liveConv = new NotebookConversation { NotebookId = liveNotebook, Title = "Alpha conversation", Created = DateTime.UtcNow.AddHours(-2) };
            db.NotebookConversations.Add(liveConv);
            await db.SaveChangesAsync();
            AddTurn(db, liveConv.Id, 1);
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = liveConv.Id,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                UserId = currentUserId,
                Content = "hello",
                Created = DateTime.UtcNow.AddHours(-1)
            });

            var (_, otherNotebook) = await SeedProjectNotebookAsync(db);
            var otherConv = new NotebookConversation { NotebookId = otherNotebook, Title = "Beta conversation", Created = DateTime.UtcNow.AddHours(-3) };
            db.NotebookConversations.Add(otherConv);
            await db.SaveChangesAsync();
            AddTurn(db, otherConv.Id, 1);
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = otherConv.Id,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                UserId = currentUserId,
                Content = "hi",
                Created = DateTime.UtcNow.AddMinutes(-30)
            });

            // Another user's live conversation must not appear in this user's list.
            var (_, otherUserNotebook) = await SeedProjectNotebookAsync(db);
            var otherUserConv = new NotebookConversation { NotebookId = otherUserNotebook, Title = "Gamma other user", Created = DateTime.UtcNow };
            db.NotebookConversations.Add(otherUserConv);
            await db.SaveChangesAsync();
            AddTurn(db, otherUserConv.Id, 1);
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = otherUserConv.Id,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                UserId = otherUserId,
                Content = "not mine",
                Created = DateTime.UtcNow
            });

            // Deleted project conversation must be excluded.
            var (_, deletedNotebook) = await SeedProjectNotebookAsync(db, projectDeleted: true);
            var deletedConv = new NotebookConversation { NotebookId = deletedNotebook, Title = "Alpha hidden", Created = DateTime.UtcNow };
            db.NotebookConversations.Add(deletedConv);
            await db.SaveChangesAsync();
            AddTurn(db, deletedConv.Id, 1);
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = deletedConv.Id,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                UserId = currentUserId,
                Content = "secret",
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // No search: both live conversations, ordered by last activity desc (Beta newer).
        var all = await Client.GetFromJsonAsync<PagedUserConversationsDto>(
            "/api/conversations?page=1&pageSize=50&sortBy=date&sortOrder=desc");
        all.Should().NotBeNull();
        all!.Items.Should().HaveCount(2);
        all.Items.Should().NotContain(i => i.Title == "Alpha hidden");
        all.Items.Should().NotContain(i => i.Title == "Gamma other user");
        all.Items[0].Title.Should().Be("Beta conversation");
        all.TotalCount.Should().Be(2);

        // Search filter (case-insensitive).
        var search = await Client.GetFromJsonAsync<PagedUserConversationsDto>(
            "/api/conversations?search=alpha&page=1&pageSize=50");
        search.Should().NotBeNull();
        search!.Items.Should().ContainSingle(i => i.Title == "Alpha conversation");

        // Sort by title-bearing column ascending (date asc here) + pagination.
        var firstPage = await Client.GetFromJsonAsync<PagedUserConversationsDto>(
            "/api/conversations?page=1&pageSize=1&sortBy=date&sortOrder=asc");
        firstPage.Should().NotBeNull();
        firstPage!.Items.Should().HaveCount(1);
        firstPage.TotalPages.Should().Be(2);
        // asc by last activity: Alpha (-1h) precedes Beta (-30m)
        firstPage.Items[0].Title.Should().Be("Alpha conversation");

        // Sort by project ascending exercises that branch.
        var byProject = await Client.GetFromJsonAsync<PagedUserConversationsDto>(
            "/api/conversations?sortBy=project&sortOrder=asc&page=1&pageSize=50");
        byProject.Should().NotBeNull();
        byProject!.Items.Should().HaveCount(2);

        // Sort by notebook descending exercises that branch.
        var byNotebook = await Client.GetFromJsonAsync<PagedUserConversationsDto>(
            "/api/conversations?sortBy=notebook&sortOrder=desc&page=1&pageSize=50");
        byNotebook.Should().NotBeNull();
        byNotebook!.Items.Should().HaveCount(2);
    }
}
