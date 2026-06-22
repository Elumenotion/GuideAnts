using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Queries;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using GuideAntsApi.Tests.BackgroundJobs;
using AntRunner.Chat.Abstractions;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.Json;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class PublishedConversationServiceTests
{
    [TestMethod]
    public async Task CreateConversationAsync_Throws_when_notebook_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-missing-{Guid.NewGuid():N}");
        var provider = CreateServiceProvider(options);

        var service = CreateService(provider);

        var act = async () => await service.CreateConversationAsync(Guid.NewGuid(), "Test");

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage("*Notebook not found*");
    }

    [TestMethod]
    public async Task CreateConversationAsync_Creates_conversation_for_existing_notebook()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-create-{Guid.NewGuid():N}");
        var notebookId = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(options))
        {
            var (projectId, nbId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            notebookId = nbId;
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(notebookId, "Published chat");

        created.Title.Should().Be("Published chat");
        await using var verify = new ApplicationDbContext(options);
        verify.NotebookConversations.Should().ContainSingle(c => c.Id == created.Id);
    }

    [TestMethod]
    public async Task CreateConversationAsync_Uses_untitled_when_title_blank()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-untitled-{Guid.NewGuid():N}");
        Guid notebookId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(notebookId, "   ");

        created.Title.Should().Be("Untitled");
    }

    [TestMethod]
    public async Task CreateConversationAsync_Trims_title_whitespace()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-trim-{Guid.NewGuid():N}");
        Guid notebookId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var created = await service.CreateConversationAsync(notebookId, "  Trimmed title  ");

        created.Title.Should().Be("Trimmed title");
    }

    [TestMethod]
    public async Task GetConversationWithMessagesAsync_Filters_duplicate_assistant_rows_without_tool_calls()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-filter-{Guid.NewGuid():N}");
        Guid conversationId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, var notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var conversation = new NotebookConversation
            {
                NotebookId = notebookId,
                Title = "Conversation with duplicates"
            };
            seed.NotebookConversations.Add(conversation);
            await seed.SaveChangesAsync();
            conversationId = conversation.Id;

            var toolCallsJson = JsonSerializer.Serialize(
                new List<ChatToolCall>
                {
                    new()
                    {
                        Id = "call_1",
                        Type = "function",
                        Function = new ChatToolCallFunction
                        {
                            Name = "SearchDocs",
                            Arguments = JsonSerializer.SerializeToElement(new { query = "filters" })
                        }
                    }
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "Same final answer",
                Created = DateTime.UtcNow
            });

            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "Same final answer",
                ToolCalls = toolCallsJson,
                Created = DateTime.UtcNow.AddSeconds(1)
            });

            await seed.SaveChangesAsync();
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var result = await service.GetConversationWithMessagesAsync(conversationId);

        result.Should().NotBeNull();
        var assistantMessages = result!.Messages.Where(m => m.Role == DataModelChatRole.Assistant).ToList();
        assistantMessages.Should().HaveCount(1);
        assistantMessages[0].ToolCalls.Should().NotBeNull();
        assistantMessages[0].ToolCalls!.Should().ContainSingle(c => c.Function.Name == "SearchDocs");
    }

    [TestMethod]
    public async Task GetConversationWithMessagesAsync_Uses_last_assistant_message_when_turns_do_not_exist()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-last-assistant-{Guid.NewGuid():N}");
        Guid conversationId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, var notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var conversation = new NotebookConversation
            {
                NotebookId = notebookId,
                Title = "No turns yet"
            };
            seed.NotebookConversations.Add(conversation);
            await seed.SaveChangesAsync();
            conversationId = conversation.Id;

            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                AssistantName = "Search",
                Content = "Result",
                Created = DateTime.UtcNow
            });

            await seed.SaveChangesAsync();
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var result = await service.GetConversationWithMessagesAsync(conversationId);

        result.Should().NotBeNull();
        result!.AssistantName.Should().Be("Search");
    }

    [TestMethod]
    public async Task UndoLastForConversationAsync_Removes_only_latest_turn_data()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-undo-{Guid.NewGuid():N}");
        Guid conversationId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, var notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var conversation = new NotebookConversation
            {
                NotebookId = notebookId,
                Title = "Undo test"
            };
            seed.NotebookConversations.Add(conversation);
            await seed.SaveChangesAsync();
            conversationId = conversation.Id;

            seed.ConversationTurns.AddRange(
                new ConversationTurn
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    AssistantName = "assistant",
                    Instructions = "first",
                    Created = DateTime.UtcNow.AddMinutes(-2)
                },
                new ConversationTurn
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 2,
                    AssistantName = "assistant",
                    Instructions = "second",
                    Created = DateTime.UtcNow.AddMinutes(-1)
                });

            seed.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 1,
                    Role = DataModelChatRole.User,
                    Content = "first user",
                    Created = DateTime.UtcNow.AddMinutes(-2)
                },
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "first response",
                    Created = DateTime.UtcNow.AddMinutes(-2).AddSeconds(1)
                },
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 2,
                    MessageSequence = 1,
                    Role = DataModelChatRole.User,
                    Content = "second user",
                    Created = DateTime.UtcNow.AddMinutes(-1)
                },
                new NotebookConversationMessage
                {
                    NotebookConversationId = conversationId,
                    TurnIndex = 2,
                    MessageSequence = 2,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "assistant",
                    Content = "second response",
                    Created = DateTime.UtcNow.AddMinutes(-1).AddSeconds(1)
                });

            await seed.SaveChangesAsync();
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        await service.UndoLastForConversationAsync(conversationId);

        await using var verify = new ApplicationDbContext(options);
        var remainingTurns = await verify.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .OrderBy(t => t.TurnIndex)
            .ToListAsync();
        var remainingMessages = await verify.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId)
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .ToListAsync();

        remainingTurns.Should().ContainSingle(t => t.TurnIndex == 1);
        remainingMessages.Should().HaveCount(2);
        remainingMessages.Select(m => m.TurnIndex).Should().OnlyContain(i => i == 1);
        remainingMessages.Should().ContainSingle(m => m.Role == DataModelChatRole.User && m.Content == "first user");
        remainingMessages.Should().ContainSingle(m => m.Role == DataModelChatRole.Assistant && m.Content == "first response");
    }

    [TestMethod]
    public async Task GetConversationWithMessagesAsync_Returns_null_for_missing_conversation()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-missing-read-{Guid.NewGuid():N}");
        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var result = await service.GetConversationWithMessagesAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetConversationWithMessagesAsync_Ignores_invalid_tool_calls_and_maps_attachment_types()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-attachments-{Guid.NewGuid():N}");
        Guid conversationId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, var notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var conversation = new NotebookConversation
            {
                NotebookId = notebookId,
                Title = "Attachment mapping"
            };
            seed.NotebookConversations.Add(conversation);
            await seed.SaveChangesAsync();
            conversationId = conversation.Id;

            var message = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "Attached files",
                ToolCalls = "{not-json",
                Created = DateTime.UtcNow
            };
            seed.NotebookConversationMessages.Add(message);

            var audioFile = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = "Output/track.mp3",
                FileSize = 512,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash-audio"
            };
            audioFile.GenerateDocumentId(notebookId);
            var binaryFile = new NotebookFile
            {
                NotebookId = notebookId,
                RelativePath = "Output/archive.bin",
                FileSize = 1024,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "hash-bin"
            };
            binaryFile.GenerateDocumentId(notebookId);
            seed.NotebookFiles.AddRange(audioFile, binaryFile);
            await seed.SaveChangesAsync();

            seed.MessageAttachments.AddRange(
                new MessageAttachment
                {
                    MessageId = message.Id,
                    NotebookFileId = audioFile.Id,
                    Type = AttachmentType.Referenced,
                    OrderIndex = 0,
                    Created = DateTime.UtcNow
                },
                new MessageAttachment
                {
                    MessageId = message.Id,
                    NotebookFileId = binaryFile.Id,
                    Type = AttachmentType.Created,
                    OrderIndex = 1,
                    Created = DateTime.UtcNow
                });

            await seed.SaveChangesAsync();
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var result = await service.GetConversationWithMessagesAsync(conversationId);

        result.Should().NotBeNull();
        var messageDto = result!.Messages.Should().ContainSingle().Subject;
        messageDto.ToolCalls.Should().BeNull();
        messageDto.Attachments.Should().HaveCount(2);
        messageDto.Attachments!.Should().Contain(a => a.FileName == "track.mp3" && a.FileType == "audio");
        messageDto.Attachments.Should().Contain(a => a.FileName == "archive.bin" && a.FileType == "other");
    }

    [TestMethod]
    public async Task UndoLastForConversationAsync_NoOps_when_conversation_has_no_user_messages()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-undo-no-user-{Guid.NewGuid():N}");
        Guid conversationId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, var notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var conversation = new NotebookConversation
            {
                NotebookId = notebookId,
                Title = "Undo no user"
            };
            seed.NotebookConversations.Add(conversation);
            await seed.SaveChangesAsync();
            conversationId = conversation.Id;

            seed.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                AssistantName = "assistant",
                Instructions = "assistant-only turn",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = "No user message",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);
        await service.UndoLastForConversationAsync(conversationId);

        await using var verify = new ApplicationDbContext(options);
        (await verify.ConversationTurns.Where(t => t.NotebookConversationId == conversationId).CountAsync()).Should().Be(1);
        (await verify.NotebookConversationMessages.Where(m => m.NotebookConversationId == conversationId).CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task UndoLastForConversationAsync_NoOps_when_conversation_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-conv-undo-missing-{Guid.NewGuid():N}");
        var provider = CreateServiceProvider(options);
        var service = CreateService(provider);

        var act = () => service.UndoLastForConversationAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    private static ServiceProvider CreateServiceProvider(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        var dbFactory = new TestDbContextFactory(options);
        services.AddSingleton(dbFactory);
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(_ => dbFactory);
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        services.AddSingleton<IServiceScopeFactory>(sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            return new TestServiceScopeFactory(db);
        });
        services.AddLogging();
        services.AddScoped<IConversationQueryService, ConversationQueryService>();
        services.AddScoped<IConversationCommandService, ConversationCommandService>();
        services.AddScoped<IAttachmentContentService, AttachmentContentService>();
        services.AddScoped<IConversationHistoryBuilder, ConversationHistoryBuilder>();
        services.AddScoped<IConversationPersistence, ConversationPersistence>();
        services.AddScoped<IConversationUsageReporter, ConversationUsageReporter>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton<IUsageRecorder>(Mock.Of<IUsageRecorder>());
        services.AddSingleton<IContextOptionsService>(Mock.Of<IContextOptionsService>());
        services.AddSingleton<IChatModelResolver>(Mock.Of<IChatModelResolver>());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new MarkdownAttachmentOptions()));
        return services.BuildServiceProvider();
    }

    private static PublishedConversationService CreateService(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        var scopeFactory = new TestServiceScopeFactory(db);
        return ConversationTestServices.CreatePublishedConversationService(
            scopeFactory,
            provider.GetRequiredService<IChatModelResolver>(),
            provider.GetRequiredService<IConversationQueryService>(),
            provider.GetRequiredService<IConversationCommandService>(),
            provider.GetRequiredService<IConversationHistoryBuilder>(),
            provider.GetRequiredService<IAttachmentContentService>(),
            provider.GetRequiredService<IConversationPersistence>(),
            provider.GetRequiredService<IConversationUsageReporter>(),
            Mock.Of<IChatCompletionClientFactory>());
    }
}
