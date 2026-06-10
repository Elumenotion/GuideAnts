using System.Text.Json;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Real-SQL integration coverage for <see cref="PublishedConversationService"/> read/persistence
/// paths (relational projection with user/edit-history joins, attachment mapping, undo turn deletes)
/// that EF-InMemory cannot reliably model. Streaming LLM paths are intentionally excluded.
/// </summary>
[TestClass]
public sealed class PublishedConversationServiceIntegrationTests : BaseEndpointTest
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

    private static IPublishedConversationService ResolveService(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IPublishedConversationService>();

    private static async Task<Guid> SeedNotebookAsync(ApplicationDbContext db)
    {
        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive)
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"Pub Conv Project {Guid.NewGuid():N}",
            Slug = $"pub-{Guid.NewGuid():N}",
            Description = "integration",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"Pub Conv Notebook {Guid.NewGuid():N}",
            Slug = $"pubnb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        return notebook.Id;
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
                    Arguments = JsonSerializer.SerializeToElement(new { q = "v" })
                }
            }
        };
        return JsonSerializer.Serialize(toolCalls, CamelCase);
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
    public async Task CreateConversation_Creates_with_trim_and_untitled_defaults()
    {
        Guid notebookId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            notebookId = await SeedNotebookAsync(db);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);

        var trimmed = await service.CreateConversationAsync(notebookId, "  Published chat  ");
        trimmed.Title.Should().Be("Published chat");

        var untitled = await service.CreateConversationAsync(notebookId, "   ");
        untitled.Title.Should().Be("Untitled");

        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.NotebookConversations.CountAsync(c => c.NotebookId == notebookId)).Should().Be(2);
    }

    // ----- GetConversationWithMessagesAsync -----

    [TestMethod]
    public async Task GetConversationWithMessages_Returns_null_for_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var result = await svc.GetConversationWithMessagesAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetConversationWithMessages_Projects_user_join_edit_history_and_attachments()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var notebookId = await SeedNotebookAsync(db);

            var conversation = new NotebookConversation { NotebookId = notebookId, Title = "Published projection" };
            db.NotebookConversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
            AddTurn(db, conversationId, 1);

            var user = new User
            {
                Id = Guid.NewGuid(),
                Name = "Pub User",
                Email = "pub.user@example.com",
                PasswordHash = "x"
            };
            db.Users.Add(user);

            var userMessage = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = "published question",
                UserId = user.Id,
                Created = DateTime.UtcNow
            };
            var assistantMessage = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "published answer",
                ToolCalls = SerializeToolCalls("call_pub", "DoThing"),
                IsEdited = true,
                LastEditedAt = DateTime.UtcNow,
                Created = DateTime.UtcNow.AddSeconds(1)
            };
            // streaming row excluded by projection
            var streaming = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 3,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "streaming row",
                IsStreaming = true,
                Created = DateTime.UtcNow.AddSeconds(2)
            };
            db.NotebookConversationMessages.AddRange(userMessage, assistantMessage, streaming);
            await db.SaveChangesAsync();

            db.MessageEditHistories.Add(new MessageEditHistory
            {
                Id = Guid.NewGuid(),
                MessageId = assistantMessage.Id,
                OriginalContent = "original published answer",
                FirstEditedByUserId = user.Id,
                FirstEditedAt = DateTime.UtcNow
            });

            var textFile = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = "Output/report.txt",
                FileSize = 42,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash-txt"
            };
            textFile.GenerateDocumentId(notebookId);
            db.NotebookFiles.Add(textFile);
            await db.SaveChangesAsync();

            db.MessageAttachments.Add(new MessageAttachment
            {
                MessageId = userMessage.Id,
                NotebookFileId = textFile.Id,
                Type = AttachmentType.Referenced,
                OrderIndex = 0,
                Created = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);
        var dto = await service.GetConversationWithMessagesAsync(conversationId);

        dto.Should().NotBeNull();
        dto!.AssistantName.Should().Be("assistant");
        dto.Messages.Should().NotContain(m => m.Content == "streaming row");

        var userDto = dto.Messages.Should().ContainSingle(m => m.Role == DataModelChatRole.User).Subject;
        userDto.UserName.Should().Be("Pub User");
        userDto.UserEmail.Should().Be("pub.user@example.com");
        userDto.Attachments.Should().ContainSingle(a => a.FileName == "report.txt" && a.FileType == "text");

        var assistantDto = dto.Messages.Should().ContainSingle(m => m.Role == DataModelChatRole.Assistant).Subject;
        assistantDto.ToolCalls.Should().ContainSingle(c => c.Function.Name == "DoThing");
        assistantDto.OriginalContent.Should().Be("original published answer");
    }

    [TestMethod]
    public async Task GetConversationWithMessages_Filters_duplicate_assistant_without_tool_calls()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var notebookId = await SeedNotebookAsync(db);
            var conversation = new NotebookConversation { NotebookId = notebookId, Title = "Duplicates" };
            db.NotebookConversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
            AddTurn(db, conversationId, 1);

            db.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 1,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "Final answer",
                    Created = DateTime.UtcNow
                },
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "Final answer",
                    ToolCalls = SerializeToolCalls("call_x", "Lookup"),
                    Created = DateTime.UtcNow.AddSeconds(1)
                });
            await db.SaveChangesAsync();
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = ResolveService(verifyScope);
        var dto = await service.GetConversationWithMessagesAsync(conversationId);

        dto.Should().NotBeNull();
        var assistants = dto!.Messages.Where(m => m.Role == DataModelChatRole.Assistant).ToList();
        assistants.Should().HaveCount(1);
        assistants[0].ToolCalls.Should().NotBeNull();
    }

    // ----- UndoLastForConversationAsync -----

    [TestMethod]
    public async Task UndoLast_NoOp_when_conversation_missing()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var svc = ResolveService(scope);

        var act = () => svc.UndoLastForConversationAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task UndoLast_NoOp_when_no_user_messages()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var notebookId = await SeedNotebookAsync(db);
            var conversation = new NotebookConversation { NotebookId = notebookId, Title = "Undo no-user" };
            db.NotebookConversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;
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
            var notebookId = await SeedNotebookAsync(db);
            var conversation = new NotebookConversation { NotebookId = notebookId, Title = "Undo latest" };
            db.NotebookConversations.Add(conversation);
            await db.SaveChangesAsync();
            conversationId = conversation.Id;

            db.ConversationTurns.AddRange(
                new ConversationTurn { NotebookConversationId = conversationId, TurnIndex = 1, AssistantName = "assistant", Instructions = "first", Created = DateTime.UtcNow.AddMinutes(-2), LastUpdated = DateTime.UtcNow.AddMinutes(-2) },
                new ConversationTurn { NotebookConversationId = conversationId, TurnIndex = 2, AssistantName = "assistant", Instructions = "second", Created = DateTime.UtcNow.AddMinutes(-1), LastUpdated = DateTime.UtcNow.AddMinutes(-1) });

            db.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 1, MessageSequence = 1, Role = DataModelChatRole.User, Content = "first user", Created = DateTime.UtcNow.AddMinutes(-2) },
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 1, MessageSequence = 2, Role = DataModelChatRole.Assistant, AssistantName = "assistant", Content = "first answer", Created = DateTime.UtcNow.AddMinutes(-2).AddSeconds(1) },
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 2, MessageSequence = 1, Role = DataModelChatRole.User, Content = "second user", Created = DateTime.UtcNow.AddMinutes(-1) },
                new NotebookConversationMessage { NotebookConversationId = conversationId, TurnIndex = 2, MessageSequence = 2, Role = DataModelChatRole.Assistant, AssistantName = "assistant", Content = "second answer", Created = DateTime.UtcNow.AddMinutes(-1).AddSeconds(1) });
            await db.SaveChangesAsync();
        }

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            await ResolveService(scope).UndoLastForConversationAsync(conversationId);
        }

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db2.ConversationTurns.CountAsync(t => t.NotebookConversationId == conversationId)).Should().Be(1);
        var messages = await db2.NotebookConversationMessages.Where(m => m.NotebookConversationId == conversationId).ToListAsync();
        messages.Should().OnlyContain(m => m.TurnIndex == 1);
    }
}
