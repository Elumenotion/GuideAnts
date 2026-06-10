using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
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
/// Real-SQL integration coverage for <see cref="ConversationService"/>.
/// These exercise the relational/SQL-only paths (READ UNCOMMITTED hints, UsageEvent
/// subqueries, FK-driven lock checks, GroupBy projections) that EF-InMemory cannot run.
/// </summary>
[TestClass]
public sealed class ConversationServiceIntegrationTests : BaseEndpointTest
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
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
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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
                Content = "hi",
                Created = DateTime.UtcNow.AddMinutes(-30)
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
                Content = "secret",
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);

        // No search: both live conversations, ordered by last activity desc (Beta newer).
        var all = await service.GetUserConversationsAsync(new UserConversationsQuery { Page = 1, PageSize = 50, SortBy = "date", SortOrder = "desc" });
        all.Items.Should().HaveCount(2);
        all.Items.Should().NotContain(i => i.Title == "Alpha hidden");
        all.Items[0].Title.Should().Be("Beta conversation");
        all.TotalCount.Should().Be(2);

        // Search filter (case-insensitive).
        var search = await service.GetUserConversationsAsync(new UserConversationsQuery { Search = "alpha", Page = 1, PageSize = 50 });
        search.Items.Should().ContainSingle(i => i.Title == "Alpha conversation");

        // Sort by title-bearing column ascending (date asc here) + pagination.
        var firstPage = await service.GetUserConversationsAsync(new UserConversationsQuery { Page = 1, PageSize = 1, SortBy = "date", SortOrder = "asc" });
        firstPage.Items.Should().HaveCount(1);
        firstPage.TotalPages.Should().Be(2);
        // asc by last activity: Alpha (-1h) precedes Beta (-30m)
        firstPage.Items[0].Title.Should().Be("Alpha conversation");

        // Sort by project ascending exercises that branch.
        var byProject = await service.GetUserConversationsAsync(new UserConversationsQuery { SortBy = "project", SortOrder = "asc", Page = 1, PageSize = 50 });
        byProject.Items.Should().HaveCount(2);

        // Sort by notebook descending exercises that branch.
        var byNotebook = await service.GetUserConversationsAsync(new UserConversationsQuery { SortBy = "notebook", SortOrder = "desc", Page = 1, PageSize = 50 });
        byNotebook.Items.Should().HaveCount(2);
    }
}
