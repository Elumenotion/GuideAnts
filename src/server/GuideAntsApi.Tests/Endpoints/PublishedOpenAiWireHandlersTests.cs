using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GuideAntsApi.Tests.Endpoints;

[TestClass]
public sealed class PublishedOpenAiWireHandlersTests
{
    [TestMethod]
    public async Task GetModelsAsync_Returns_enabled_aliases_only()
    {
        var pubId = Guid.NewGuid();
        var context = CreateExecutionContext(
            pubId,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guide"] = "guide-alias",
                    ["embeddings"] = "embeddings-alias",
                    ["image"] = "image-alias"
                },
                EndpointFlags = new PublishedWireApiEndpointFlagsDto
                {
                    Models = true,
                    ChatCompletions = true,
                    Responses = true,
                    Embeddings = true,
                    ImageGenerations = false,
                    AudioTranscriptions = false,
                    AudioSpeech = false
                }
            });

        var resolver = new StubResolver(context);
        var http = new DefaultHttpContext();

        var result = await PublishedOpenAiWireHandlers.GetModelsAsync(http, pubId, resolver);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var data = json.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(2);
        data.EnumerateArray().Select(x => x.GetProperty("id").GetString()).Should().BeEquivalentTo(
            ["embeddings-alias", "guide-alias"],
            options => options.WithoutStrictOrdering());
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Returns_openai_sse_for_streaming()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Hello streamed\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Stream = true,
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        executed.Body.Should().Contain("\"object\":\"chat.completion.chunk\"");
        executed.Body.Should().Contain("\"finish_reason\":\"stop\"");
        executed.Body.Should().Contain("data: [DONE]");
        executed.Body.Should().Contain("\"content\":\"Hello streamed\"");
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Rewrites_published_links_with_request_origin()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var relativePublishedUrl =
            $"/api/published/projects/{Guid.NewGuid()}/notebooks/{notebookId}/conversations/{conversationId}/files/content?path=audio%2Fnote.mp3&pubId={pubId}";
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, $"{{\"content\":\"[audio]({relativePublishedUrl})\"}}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("wire.example.com");
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        content.Should().Contain($"https://wire.example.com{relativePublishedUrl}");
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Forwards_client_messages_prefix_and_last_user_prompt()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "final user prompt" &&
                    r.ClientMessages != null &&
                    r.ClientMessages.Count == 3 &&
                    r.ClientMessages[0].Role == AntRunner.Chat.Abstractions.ChatRole.System &&
                    ReadMessageText(r.ClientMessages[0]) == "client system" &&
                    r.ClientMessages[1].Role == AntRunner.Chat.Abstractions.ChatRole.User &&
                    ReadMessageText(r.ClientMessages[1]) == "earlier user" &&
                    r.ClientMessages[2].Role == AntRunner.Chat.Abstractions.ChatRole.Assistant &&
                    ReadMessageText(r.ClientMessages[2]) == "earlier assistant"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"ok\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":1}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  { "role": "system", "content": "client system" },
                  { "role": "user", "content": "earlier user" },
                  { "role": "assistant", "content": "earlier assistant" },
                  { "role": "user", "content": "final user prompt" }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.VerifyAll();
    }

    [TestMethod]
    public async Task PostResponsesAsync_Returns_openai_sse_for_streaming()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Hello response stream\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":5}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Stream = true,
            Input = ParseJsonElement("\"hello\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        executed.Body.Should().Contain("event: response.created");
        executed.Body.Should().Contain("event: response.output_text.delta");
        executed.Body.Should().Contain("event: response.completed");
        executed.Body.Should().Contain("\"delta\":\"Hello response stream\"");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Rewrites_published_links_with_request_origin()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var relativePublishedUrl =
            $"/api/published/projects/{Guid.NewGuid()}/notebooks/{notebookId}/conversations/{conversationId}/files/content?path=audio%2Fnote.mp3&pubId={pubId}";
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, $"{{\"content\":\"[audio]({relativePublishedUrl})\"}}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":5}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("wire.example.com");
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"hello\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var content = json.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString();
        content.Should().Contain($"https://wire.example.com{relativePublishedUrl}");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Forwards_client_messages_prefix_and_last_user_prompt()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "new user turn" &&
                    r.ClientMessages != null &&
                    r.ClientMessages.Count == 2 &&
                    r.ClientMessages[0].Role == AntRunner.Chat.Abstractions.ChatRole.System &&
                    ReadMessageText(r.ClientMessages[0]) == "client system" &&
                    r.ClientMessages[1].Role == AntRunner.Chat.Abstractions.ChatRole.Assistant &&
                    ReadMessageText(r.ClientMessages[1]) == "prior assistant"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"ok\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":4,\"completion_tokens\":1}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("""
                [
                  {
                    "type": "message",
                    "role": "system",
                    "content": [{ "type": "input_text", "text": "client system" }]
                  },
                  {
                    "type": "message",
                    "role": "assistant",
                    "content": [{ "type": "output_text", "text": "prior assistant" }]
                  },
                  {
                    "type": "message",
                    "role": "user",
                    "content": [{ "type": "input_text", "text": "new user turn" }]
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.VerifyAll();
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Returns_tool_calls_when_pending_client_tool()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.ClientToolDefinitions != null &&
                    r.ClientToolDefinitions.Count == 1 &&
                    r.ClientToolDefinitions[0].Function != null &&
                    r.ClientToolDefinitions[0].Function.Name == "run_shell"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Let me check.\"}"),
                new StreamingEvent(
                    StreamingEventTypes.ExternalToolCall,
                    "{\"toolCalls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]}"),
                new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":5,\"completion_tokens\":4}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"where am i?\"}]"),
            Tools = ParseJsonElement("""
                [
                  {
                    "type": "function",
                    "function": {
                      "name": "run_shell",
                      "description": "Execute a shell command",
                      "parameters": {
                        "type": "object",
                        "properties": {
                          "command": { "type": "string" }
                        },
                        "required": ["command"]
                      }
                    }
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var choice = json.RootElement.GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("tool_calls");
        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
        toolCall.GetProperty("id").GetString().Should().Be("call_1");
        toolCall.GetProperty("function").GetProperty("name").GetString().Should().Be("run_shell");
        toolCall.GetProperty("function").GetProperty("arguments").GetString().Should().Contain("\"command\":\"pwd\"");
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Resumes_pending_turn_when_tool_message_is_provided()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-chat-openai"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-chat-openai"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = now
        };
        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "where am i?",
            Status = "streaming",
            Created = now,
            LastUpdated = now
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.ConversationTurns.Add(turn);
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.User,
            Content = "where am i?",
            TurnIndex = 1,
            MessageSequence = 1,
            ExternalUserIdentity = "user",
            Created = now
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.Assistant,
            Content = string.Empty,
            TurnIndex = 1,
            MessageSequence = 2,
            AssistantName = "Guide",
            ToolCalls = "[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]",
            IsStreaming = false,
            Created = now.AddSeconds(1)
        });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.ResumeAfterExternalToolResultsStreamAsync(
                conversationId,
                pubId.ToString(),
                "user",
                null,
                It.Is<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(tools =>
                    tools != null &&
                    tools.Count == 1 &&
                    tools[0].Function != null &&
                    tools[0].Function.Name == "run_shell"),
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"You are in D:/repos/GuideAnts\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":9,\"completion_tokens\":5}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "run_shell",
                          "arguments": "{\"command\":\"pwd\"}"
                        }
                      }
                    ]
                  },
                  {
                    "role": "tool",
                    "tool_call_id": "call_1",
                    "name": "run_shell",
                    "content": "D:/repos/GuideAnts"
                  }
                ]
                """),
            Tools = ParseJsonElement("""
                [
                  {
                    "type": "function",
                    "function": {
                      "name": "run_shell",
                      "description": "Execute a shell command",
                      "parameters": {
                        "type": "object",
                        "properties": {
                          "command": { "type": "string" }
                        },
                        "required": ["command"]
                      }
                    }
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var choice = json.RootElement.GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("stop");
        choice.GetProperty("message").GetProperty("content").GetString().Should().Be("You are in D:/repos/GuideAnts");

        conversationService.Verify(
            s => s.ResumeAfterExternalToolResultsStreamAsync(
                conversationId,
                pubId.ToString(),
                "user",
                null,
                It.IsAny<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                It.IsAny<Guid>(),
                It.IsAny<SendMessageRequest>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostResponsesAsync_Returns_function_call_output_when_pending_client_tool()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.ClientToolDefinitions != null &&
                    r.ClientToolDefinitions.Count == 1 &&
                    r.ClientToolDefinitions[0].Function != null &&
                    r.ClientToolDefinitions[0].Function.Name == "run_shell"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Let me check.\"}"),
                new StreamingEvent(
                    StreamingEventTypes.ExternalToolCall,
                    "{\"toolCalls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]}"),
                new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":6,\"completion_tokens\":4}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"where am i?\""),
            Tools = ParseJsonElement("""
                [
                  {
                    "type": "function",
                    "name": "run_shell",
                    "description": "Execute a shell command",
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "command": { "type": "string" }
                      },
                      "required": ["command"]
                    }
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var output = json.RootElement.GetProperty("output");
        output.EnumerateArray().Any(item =>
            item.GetProperty("type").GetString() == "function_call" &&
            item.GetProperty("call_id").GetString() == "call_1" &&
            item.GetProperty("name").GetString() == "run_shell").Should().BeTrue();
    }

    [TestMethod]
    public async Task PostResponsesAsync_Resumes_pending_turn_when_function_call_output_is_provided()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-responses-openai"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-responses-openai"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = now
        };
        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "where am i?",
            Status = "streaming",
            Created = now,
            LastUpdated = now
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.ConversationTurns.Add(turn);
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.User,
            Content = "where am i?",
            TurnIndex = 1,
            MessageSequence = 1,
            ExternalUserIdentity = "user",
            Created = now
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.Assistant,
            Content = string.Empty,
            TurnIndex = 1,
            MessageSequence = 2,
            AssistantName = "Guide",
            ToolCalls = "[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]",
            IsStreaming = false,
            Created = now.AddSeconds(1)
        });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.ResumeAfterExternalToolResultsStreamAsync(
                conversationId,
                pubId.ToString(),
                "user",
                null,
                It.Is<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(tools =>
                    tools != null &&
                    tools.Count == 1 &&
                    tools[0].Function != null &&
                    tools[0].Function.Name == "run_shell"),
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"You are in D:/repos/GuideAnts\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":10,\"completion_tokens\":6}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("""
                [
                  {
                    "type": "function_call_output",
                    "call_id": "call_1",
                    "name": "run_shell",
                    "output": "D:/repos/GuideAnts"
                  }
                ]
                """),
            Tools = ParseJsonElement("""
                [
                  {
                    "type": "function",
                    "name": "run_shell",
                    "description": "Execute a shell command",
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "command": { "type": "string" }
                      },
                      "required": ["command"]
                    }
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var output = json.RootElement.GetProperty("output");
        output.EnumerateArray().Any(item =>
            item.GetProperty("type").GetString() == "message" &&
            item.GetProperty("content")[0].GetProperty("text").GetString() == "You are in D:/repos/GuideAnts").Should().BeTrue();

        conversationService.Verify(
            s => s.ResumeAfterExternalToolResultsStreamAsync(
                conversationId,
                pubId.ToString(),
                "user",
                null,
                It.IsAny<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                It.IsAny<Guid>(),
                It.IsAny<SendMessageRequest>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Returns_anthropic_sse_for_streaming()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Hello streamed\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":4,\"completion_tokens\":6}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Stream = true,
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        executed.Body.Should().Contain("event: message_start");
        executed.Body.Should().Contain("event: content_block_start");
        executed.Body.Should().Contain("event: content_block_delta");
        executed.Body.Should().Contain("event: content_block_stop");
        executed.Body.Should().Contain("event: message_delta");
        executed.Body.Should().Contain("event: message_stop");
        executed.Body.Should().Contain("\"text\":\"Hello streamed\"");
    }

    [TestMethod]
    public async Task PostMessagesAsync_Rewrites_published_links_with_request_origin()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var relativePublishedUrl =
            $"/api/published/projects/{Guid.NewGuid()}/notebooks/{notebookId}/conversations/{conversationId}/files/content?path=audio%2Fnote.mp3&pubId={pubId}";
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, $"{{\"content\":\"[audio]({relativePublishedUrl})\"}}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":4,\"completion_tokens\":6}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("wire.example.com");
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var content = json.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        content.Should().Contain($"https://wire.example.com{relativePublishedUrl}");
    }

    [TestMethod]
    public async Task PostMessagesAsync_Forwards_client_messages_prefix_and_last_user_prompt()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "final user prompt" &&
                    r.ClientMessages != null &&
                    r.ClientMessages.Count == 3 &&
                    r.ClientMessages[0].Role == AntRunner.Chat.Abstractions.ChatRole.System &&
                    ReadMessageText(r.ClientMessages[0]) == "anthropic client system" &&
                    r.ClientMessages[1].Role == AntRunner.Chat.Abstractions.ChatRole.User &&
                    ReadMessageText(r.ClientMessages[1]) == "earlier user" &&
                    r.ClientMessages[2].Role == AntRunner.Chat.Abstractions.ChatRole.Assistant &&
                    ReadMessageText(r.ClientMessages[2]) == "earlier assistant"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"ok\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":5,\"completion_tokens\":1}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            System = ParseJsonElement("\"anthropic client system\""),
            Messages = ParseJsonElement("""
                [
                  {
                    "role": "user",
                    "content": [{ "type": "text", "text": "earlier user" }]
                  },
                  {
                    "role": "assistant",
                    "content": [{ "type": "text", "text": "earlier assistant" }]
                  },
                  {
                    "role": "user",
                    "content": [{ "type": "text", "text": "final user prompt" }]
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.VerifyAll();
    }

    [TestMethod]
    public async Task PostMessagesAsync_Returns_invalid_request_error_for_model_alias_mismatch()
    {
        var pubId = Guid.NewGuid();
        var context = CreateExecutionContext(
            pubId,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guide"] = "guide-alias"
                }
            });
        var resolver = new StubResolver(context);
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "wrong-alias",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("type").GetString().Should().Be("error");
        json.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("invalid_request_error");
        conversationService.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task PostMessagesAsync_Returns_anthropic_message_payload_for_success()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var context = CreateExecutionContext(
            pubId,
            notebookId: notebookId,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guide"] = "guide-alias"
                }
            });
        var resolver = new StubResolver(context);
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions.Contains("hello from messages", StringComparison.OrdinalIgnoreCase)),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Hello from guide\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":7,\"completion_tokens\":11}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide-alias",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello from messages\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("type").GetString().Should().Be("message");
        json.RootElement.GetProperty("role").GetString().Should().Be("assistant");
        json.RootElement.GetProperty("model").GetString().Should().Be("guide-alias");
        json.RootElement.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("text");
        json.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hello from guide");
        json.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt64().Should().Be(7);
        json.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt64().Should().Be(11);
        conversationService.VerifyAll();
    }

    [TestMethod]
    public async Task PostMessagesAsync_Returns_persisted_assistant_message_id()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-message-id"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-message-id"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = now
        };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        await db.SaveChangesAsync();

        async IAsyncEnumerable<StreamingEvent> PersistingStream()
        {
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = assistantMessageId,
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = "Hello from guide",
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(1)
            });
            await db.SaveChangesAsync();
            yield return new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Hello from guide\"}");
            yield return new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":7,\"completion_tokens\":11}");
        }

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(PersistingStream);

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"hello from messages\"}]")
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("id").GetString().Should().Be($"msg_{assistantMessageId:N}");
    }

    [TestMethod]
    public async Task PostMessagesAsync_Returns_tool_use_content_when_pending_client_tool()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(conversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.ClientToolDefinitions != null &&
                    r.ClientToolDefinitions.Count == 1 &&
                    r.ClientToolDefinitions[0].Function != null &&
                    r.ClientToolDefinitions[0].Function.Name == "run_shell"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Let me check that for you.\"}"),
                new StreamingEvent(
                    StreamingEventTypes.ExternalToolCall,
                    "{\"toolCalls\":[{\"id\":\"toolu_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]}"),
                new StreamingEvent(StreamingEventTypes.PendingClientTool, "{}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":9,\"completion_tokens\":4}")
            ));

        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("[{\"role\":\"user\",\"content\":\"where am i?\"}]"),
            Tools = ParseJsonElement("""
                [
                  {
                    "name": "run_shell",
                    "description": "Execute a shell command",
                    "input_schema": {
                      "type": "object",
                      "properties": {
                        "command": { "type": "string" }
                      },
                      "required": ["command"]
                    }
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("stop_reason").GetString().Should().Be("tool_use");
        var content = json.RootElement.GetProperty("content");
        content.EnumerateArray().Any(b =>
            b.GetProperty("type").GetString() == "tool_use" &&
            b.GetProperty("id").GetString() == "toolu_1" &&
            b.GetProperty("name").GetString() == "run_shell").Should().BeTrue();
    }

    [TestMethod]
    public async Task PostMessagesAsync_Resumes_pending_turn_when_tool_result_is_provided()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = now
        };
        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "where am i?",
            Status = "streaming",
            Created = now,
            LastUpdated = now
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.ConversationTurns.Add(turn);
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.User,
            Content = "where am i?",
            TurnIndex = 1,
            MessageSequence = 1,
            ExternalUserIdentity = "user",
            Created = now
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.Assistant,
            Content = string.Empty,
            TurnIndex = 1,
            MessageSequence = 2,
            AssistantName = "Guide",
            ToolCalls = "[{\"id\":\"toolu_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]",
            IsStreaming = false,
            Created = now.AddSeconds(1)
        });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.ResumeAfterExternalToolResultsStreamAsync(
                conversationId,
                pubId.ToString(),
                "user",
                null,
                It.Is<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(tools =>
                    tools != null &&
                    tools.Count == 1 &&
                    tools[0].Function != null &&
                    tools[0].Function.Name == "run_shell"),
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"You are in D:/repos/GuideAnts\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":12,\"completion_tokens\":6}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {
                    "role": "assistant",
                    "content": [
                      {
                        "type": "tool_use",
                        "id": "toolu_1",
                        "name": "run_shell",
                        "input": { "command": "pwd" }
                      }
                    ]
                  },
                  {
                    "role": "user",
                    "content": [
                      {
                        "type": "tool_result",
                        "tool_use_id": "toolu_1",
                        "content": "D:/repos/GuideAnts"
                      }
                    ]
                  }
                ]
                """),
            Tools = ParseJsonElement("""
                [
                  {
                    "name": "run_shell",
                    "description": "Execute a shell command",
                    "input_schema": {
                      "type": "object",
                      "properties": {
                        "command": { "type": "string" }
                      },
                      "required": ["command"]
                    }
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        json.RootElement.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("text");
        json.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("You are in D:/repos/GuideAnts");

        conversationService.Verify(
            s => s.ResumeAfterExternalToolResultsStreamAsync(
                conversationId,
                pubId.ToString(),
                "user",
                null,
                It.IsAny<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                It.IsAny<Guid>(),
                It.IsAny<SendMessageRequest>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Continues_conversation_from_transcript_when_history_has_no_assistant_message_id()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-messages-transcript-continuation"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-messages-transcript-continuation"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Existing conversation",
            Created = now
        };
        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "hello",
            Status = "completed",
            Created = now,
            LastUpdated = now
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.ConversationTurns.Add(turn);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.User,
                Content = "hello",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = "Hello! How can I help you today?",
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(1)
            });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "tell me about this project" &&
                    r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Project summary\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":12,\"completion_tokens\":4}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {
                    "role": "user",
                    "content": "hello"
                  },
                  {
                    "role": "assistant",
                    "content": "Hello! How can I help you today?"
                  },
                  {
                    "role": "system",
                    "content": "{\"contextOptions\":{\"system.currentDate\":\"2026-06-25\"}}"
                  },
                  {
                    "role": "user",
                    "content": "tell me about this project"
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Project summary");

        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.CreateConversationAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Continues_conversation_from_full_transcript_when_assistant_text_repeats()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var targetConversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var repeatedAssistantText = "Same assistant answer";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-messages-transcript-repeat"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-messages-transcript-repeat"
        };
        var targetConversation = new NotebookConversation
        {
            Id = targetConversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Target conversation",
            Created = now
        };
        var otherConversation = new NotebookConversation
        {
            Id = otherConversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Other conversation",
            Created = now.AddSeconds(10)
        };
        var targetTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = targetConversationId,
            NotebookConversation = targetConversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "target hello",
            Status = "completed",
            Created = now,
            LastUpdated = now
        };
        var otherTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = otherConversationId,
            NotebookConversation = otherConversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "other hello",
            Status = "completed",
            Created = now.AddSeconds(10),
            LastUpdated = now.AddSeconds(10)
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.AddRange(targetConversation, otherConversation);
        db.ConversationTurns.AddRange(targetTurn, otherTurn);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = targetConversationId,
                NotebookConversation = targetConversation,
                Role = ChatRole.User,
                Content = "target hello",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = targetConversationId,
                NotebookConversation = targetConversation,
                Role = ChatRole.Assistant,
                Content = repeatedAssistantText,
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(1)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = otherConversationId,
                NotebookConversation = otherConversation,
                Role = ChatRole.User,
                Content = "other hello",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now.AddSeconds(10)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = otherConversationId,
                NotebookConversation = otherConversation,
                Role = ChatRole.Assistant,
                Content = repeatedAssistantText,
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(11)
            });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                targetConversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "continue this one" &&
                    r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Continued target conversation\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":12,\"completion_tokens\":4}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement($$"""
                [
                  {
                    "role": "user",
                    "content": "target hello"
                  },
                  {
                    "role": "assistant",
                    "content": "{{repeatedAssistantText}}"
                  },
                  {
                    "role": "user",
                    "content": "continue this one"
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                targetConversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                otherConversationId,
                It.IsAny<SendMessageRequest>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        conversationService.Verify(
            s => s.CreateConversationAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Continues_conversation_when_persisted_history_has_internal_tool_messages()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string clientToolCallId = "client_read_1";
        const string serverToolCallId = "server_search_1";
        const string toolAssistantText = "Let me explore the project to understand what it is.";
        const string finalAssistantText = "GuideAnts is a project.";

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-messages-internal-tools"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-messages-internal-tools"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Existing conversation",
            Created = now
        };
        var firstTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "hello",
            Status = "completed",
            Created = now,
            LastUpdated = now
        };
        var secondTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 2,
            AssistantName = "Guide",
            Instructions = "describe this project",
            Status = "completed",
            Created = now.AddSeconds(2),
            LastUpdated = now.AddSeconds(6)
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.ConversationTurns.AddRange(firstTurn, secondTurn);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.User,
                Content = "hello",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = "Hello! How can I help you today?",
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(1)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.User,
                Content = "describe this project",
                TurnIndex = 2,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now.AddSeconds(2)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = toolAssistantText,
                ToolCalls = JsonSerializer.Serialize(new[]
                {
                    new AntRunner.Chat.Abstractions.ChatToolCall
                    {
                        Id = serverToolCallId,
                        Type = "function",
                        Function = new AntRunner.Chat.Abstractions.ChatToolCallFunction
                        {
                            Name = "Search",
                            Arguments = JsonSerializer.SerializeToElement(new { instructions = "Describe GuideAnts" })
                        }
                    },
                    new AntRunner.Chat.Abstractions.ChatToolCall
                    {
                        Id = clientToolCallId,
                        Type = "function",
                        Function = new AntRunner.Chat.Abstractions.ChatToolCallFunction
                        {
                            Name = "Read",
                            Arguments = JsonSerializer.SerializeToElement(new { file_path = @"D:\repos\GuideAnts\README.md" })
                        }
                    }
                }),
                TurnIndex = 2,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(3)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Tool,
                Content = "README contents",
                ToolCallId = clientToolCallId,
                FunctionName = "Read",
                TurnIndex = 2,
                MessageSequence = 3,
                Created = now.AddSeconds(4)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Tool,
                Content = "Search summary",
                ToolCallId = serverToolCallId,
                FunctionName = "Search",
                TurnIndex = 2,
                MessageSequence = 4,
                Created = now.AddSeconds(5)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = finalAssistantText,
                TurnIndex = 2,
                MessageSequence = 5,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(6)
            });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "what else?" &&
                    r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"More details\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":12,\"completion_tokens\":4}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement($$"""
                [
                  {
                    "role": "user",
                    "content": "hello"
                  },
                  {
                    "role": "assistant",
                    "content": "Hello! How can I help you today?"
                  },
                  {
                    "role": "user",
                    "content": "describe this project"
                  },
                  {
                    "role": "assistant",
                    "content": [
                      { "type": "text", "text": "{{toolAssistantText}}" },
                      {
                        "type": "tool_use",
                        "id": "{{clientToolCallId}}",
                        "name": "Read",
                        "input": { "file_path": "D:\\repos\\GuideAnts\\README.md" }
                      }
                    ]
                  },
                  {
                    "role": "user",
                    "content": [
                      {
                        "type": "tool_result",
                        "tool_use_id": "{{clientToolCallId}}",
                        "content": "README contents"
                      }
                    ]
                  },
                  {
                    "role": "assistant",
                    "content": "{{finalAssistantText}}"
                  },
                  {
                    "role": "user",
                    "content": "what else?"
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.CreateConversationAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Continues_conversation_by_assistant_message_id_when_text_repeats()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var targetConversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var targetAssistantMessageId = Guid.NewGuid();
        var repeatedAssistantText = "Same assistant answer";
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-messages-id-continuation"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-messages-id-continuation"
        };
        var targetConversation = new NotebookConversation
        {
            Id = targetConversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Target conversation",
            Created = now
        };
        var otherConversation = new NotebookConversation
        {
            Id = otherConversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Other conversation",
            Created = now.AddSeconds(10)
        };
        var targetTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = targetConversationId,
            NotebookConversation = targetConversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "hello",
            Status = "completed",
            Created = now,
            LastUpdated = now
        };
        var otherTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = otherConversationId,
            NotebookConversation = otherConversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "hello",
            Status = "completed",
            Created = now.AddSeconds(10),
            LastUpdated = now.AddSeconds(10)
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.AddRange(targetConversation, otherConversation);
        db.ConversationTurns.AddRange(targetTurn, otherTurn);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = targetConversationId,
                NotebookConversation = targetConversation,
                Role = ChatRole.User,
                Content = "hello",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now
            },
            new NotebookConversationMessage
            {
                Id = targetAssistantMessageId,
                NotebookConversationId = targetConversationId,
                NotebookConversation = targetConversation,
                Role = ChatRole.Assistant,
                Content = repeatedAssistantText,
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(1)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = otherConversationId,
                NotebookConversation = otherConversation,
                Role = ChatRole.User,
                Content = "hello",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user",
                Created = now.AddSeconds(10)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = otherConversationId,
                NotebookConversation = otherConversation,
                Role = ChatRole.Assistant,
                Content = repeatedAssistantText,
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(11)
            });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                targetConversationId,
                It.Is<SendMessageRequest>(r =>
                    r.Instructions == "continue this one" &&
                    r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Continued target conversation\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":12,\"completion_tokens\":4}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement($$"""
                [
                  {
                    "role": "user",
                    "content": "hello"
                  },
                  {
                    "id": "msg_{{targetAssistantMessageId:N}}",
                    "role": "assistant",
                    "content": "{{repeatedAssistantText}}"
                  },
                  {
                    "role": "user",
                    "content": "continue this one"
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                targetConversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                otherConversationId,
                It.IsAny<SendMessageRequest>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        conversationService.Verify(
            s => s.CreateConversationAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Ignores_historical_tool_results_when_latest_message_is_user_text()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var historicalConversationId = Guid.NewGuid();
        var newConversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-messages-history"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-messages-history"
        };
        var historicalConversation = new NotebookConversation
        {
            Id = historicalConversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Historical conversation",
            Created = now
        };
        var completedTurn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = historicalConversationId,
            NotebookConversation = historicalConversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = "where am i?",
            Status = "completed",
            Created = now,
            LastUpdated = now
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(historicalConversation);
        db.ConversationTurns.Add(completedTurn);
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = historicalConversationId,
            NotebookConversation = historicalConversation,
            Role = ChatRole.User,
            Content = "where am i?",
            TurnIndex = 1,
            MessageSequence = 1,
            ExternalUserIdentity = "user",
            Created = now
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = historicalConversationId,
            NotebookConversation = historicalConversation,
            Role = ChatRole.Assistant,
            Content = string.Empty,
            TurnIndex = 1,
            MessageSequence = 2,
            AssistantName = "Guide",
            ToolCalls = "[{\"id\":\"toolu_1\",\"type\":\"function\",\"function\":{\"name\":\"run_shell\",\"arguments\":{\"command\":\"pwd\"}}}]",
            IsStreaming = false,
            Created = now.AddSeconds(1)
        });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.CreateConversationAsync(
                notebookId,
                It.Is<string>(title => title == "New Conversation")))
            .ReturnsAsync(new NotebookConversationListDto(newConversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                newConversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "tell me about this project"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Project summary\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":13,\"completion_tokens\":5}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {
                    "role": "assistant",
                    "content": [
                      {
                        "type": "tool_use",
                        "id": "toolu_1",
                        "name": "run_shell",
                        "input": { "command": "pwd" }
                      }
                    ]
                  },
                  {
                    "role": "user",
                    "content": [
                      {
                        "type": "tool_result",
                        "tool_use_id": "toolu_1",
                        "content": "D:/repos/GuideAnts"
                      }
                    ]
                  },
                  {
                    "role": "assistant",
                    "content": "I checked the repo."
                  },
                  {
                    "role": "user",
                    "content": "tell me about this project"
                  }
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Project summary");

        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                newConversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.ResumeAfterExternalToolResultsStreamAsync(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<IReadOnlyList<AntRunner.Chat.Abstractions.ChatToolDefinition>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesCountTokensAsync_Returns_invalid_request_error_for_model_alias_mismatch()
    {
        var pubId = Guid.NewGuid();
        var context = CreateExecutionContext(
            pubId,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guide"] = "guide-alias"
                }
            });
        var resolver = new StubResolver(context);
        var http = new DefaultHttpContext();
        var request = ParseJsonElement("""
            {
              "model": "wrong-alias",
              "messages": [{ "role": "user", "content": "hello" }]
            }
            """);

        var result = await PublishedOpenAiWireHandlers.PostMessagesCountTokensAsync(
            http,
            pubId,
            request,
            resolver);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("type").GetString().Should().Be("error");
        json.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("invalid_request_error");
    }

    [TestMethod]
    public async Task PostMessagesCountTokensAsync_Returns_input_tokens_estimate()
    {
        var pubId = Guid.NewGuid();
        var context = CreateExecutionContext(
            pubId,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guide"] = "guide-alias"
                }
            });
        var resolver = new StubResolver(context);
        var http = new DefaultHttpContext();
        var request = ParseJsonElement("""
            {
              "model": "guide-alias",
              "system": "You are helpful.",
              "messages": [{ "role": "user", "content": "hello from count tokens" }]
            }
            """);

        var expectedTokens = (Encoding.UTF8.GetByteCount(request.GetRawText()) + 3L) / 4L;

        var result = await PublishedOpenAiWireHandlers.PostMessagesCountTokensAsync(
            http,
            pubId,
            request,
            resolver);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("input_tokens").GetInt64().Should().Be(expectedTokens);
    }

    [TestMethod]
    public async Task PostResponsesAsync_Returns_invalid_previous_response_id_for_bad_format()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"hello\""),
            PreviousResponseId = "not-a-wire-response-id"
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("invalid_previous_response_id");
        error.GetProperty("param").GetString().Should().Be("previous_response_id");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Continues_existing_conversation_when_previous_response_id_provided()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = now
        };
        var userMessage = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.User,
            Content = "hello",
            TurnIndex = 1,
            MessageSequence = 1,
            ExternalUserIdentity = "user-a",
            Created = now
        };
        var assistantMessage = new NotebookConversationMessage
        {
            Id = assistantMessageId,
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            Role = ChatRole.Assistant,
            Content = "hello from assistant",
            TurnIndex = 1,
            MessageSequence = 2,
            AssistantName = "Guide",
            IsStreaming = false,
            Created = now.AddSeconds(1)
        };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.NotebookConversationMessages.Add(userMessage);
        db.NotebookConversationMessages.Add(assistantMessage);
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user-a"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "follow-up"),
                pubId.ToString(),
                "user-a",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Follow-up response\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":5}")
            ));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"follow-up\""),
            PreviousResponseId = $"resp_{assistantMessageId:N}"
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("id").GetString().Should().Be($"resp_{assistantMessageId:N}");
        json.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be("Follow-up response");

        conversationService.Verify(s => s.CreateConversationAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(
                conversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "user-a",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task PostResponsesAsync_Rejects_branching_from_non_latest_previous_response_id()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var firstAssistantMessageId = Guid.NewGuid();
        var secondAssistantMessageId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = "project-branch"
        };
        var notebook = new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = "notebook-branch"
        };
        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = now
        };

        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.User,
                Content = "turn one",
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = "user-a",
                Created = now
            },
            new NotebookConversationMessage
            {
                Id = firstAssistantMessageId,
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = "assistant one",
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(1)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.User,
                Content = "turn two",
                TurnIndex = 2,
                MessageSequence = 1,
                ExternalUserIdentity = "user-a",
                Created = now.AddSeconds(2)
            },
            new NotebookConversationMessage
            {
                Id = secondAssistantMessageId,
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = "assistant two",
                TurnIndex = 2,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = now.AddSeconds(3)
            });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user-a"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"new request\""),
            PreviousResponseId = $"resp_{firstAssistantMessageId:N}"
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object,
            db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("unsupported_feature");
        error.GetProperty("param").GetString().Should().Be("previous_response_id");

        conversationService.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task PostMessagesAsync_Transcript_does_not_match_conversation_idle_longer_than_60_minutes()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var staleTime = DateTime.UtcNow.AddHours(-2);

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, staleTime, "hello", "Hi there", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var newConversationId = Guid.NewGuid();
        conversationService
            .Setup(s => s.CreateConversationAsync(notebookId, "New Conversation"))
            .ReturnsAsync(new NotebookConversationListDto(newConversationId, "wire-conversation", DateTime.UtcNow, DateTime.UtcNow));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                newConversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "follow up" && r.ClientMessages != null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"New thread\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi there"},
                  {"role":"user","content":"follow up"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(s => s.CreateConversationAsync(notebookId, "New Conversation"), Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(conversationId, It.IsAny<SendMessageRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Transcript_does_not_attach_to_different_caller_identity()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi there", "owner-user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "other-user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var newConversationId = Guid.NewGuid();
        conversationService
            .Setup(s => s.CreateConversationAsync(notebookId, "New Conversation"))
            .ReturnsAsync(new NotebookConversationListDto(newConversationId, "wire-conversation", now, now));
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                newConversationId,
                It.IsAny<SendMessageRequest>(),
                pubId.ToString(),
                "other-user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"New thread\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi there"},
                  {"role":"user","content":"follow up"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(s => s.CreateConversationAsync(notebookId, "New Conversation"), Times.Once);
    }

    [TestMethod]
    public async Task PostMessagesAsync_Transcript_short_circuits_to_later_candidate_when_earlier_diverges()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var wrongConversationId = Guid.NewGuid();
        var targetConversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, wrongConversationId, now.AddSeconds(10), "wrong hello", "Shared reply", "user");
        await SeedTranscriptConversationAsync(db, notebookId, targetConversationId, now, "target hello", "Shared reply", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                targetConversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "continue target" && r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Continued\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.AnthropicMessagesRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {"role":"user","content":"target hello"},
                  {"role":"assistant","content":"Shared reply"},
                  {"role":"user","content":"continue target"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostMessagesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(targetConversationId, It.IsAny<SendMessageRequest>(), pubId.ToString(), "user", null, It.IsAny<CancellationToken>()),
            Times.Once);
        conversationService.Verify(
            s => s.SendMessageStreamAsync(wrongConversationId, It.IsAny<SendMessageRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Continues_conversation_from_transcript_replay()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi from assistant", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "next question" && r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Answer\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi from assistant"},
                  {"role":"user","content":"next question"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(s => s.CreateConversationAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Continues_conversation_when_replay_omits_internal_tool_messages()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string finalAssistantText = "GuideAnts is a project.";

        using var db = CreateDbContext();
        var project = new Project { Id = Guid.NewGuid(), Title = "Project", Slug = "project-chat-internal-tools" };
        var notebook = new Notebook { Id = notebookId, ProjectId = project.Id, Project = project, Title = "Notebook", Slug = "notebook-chat-internal-tools" };
        var conversation = new NotebookConversation { Id = conversationId, NotebookId = notebookId, Notebook = notebook, Title = "Conversation", Created = now };
        db.Projects.Add(project);
        db.Notebooks.Add(notebook);
        db.NotebookConversations.Add(conversation);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = conversationId, NotebookConversation = conversation,
                Role = ChatRole.User, Content = "describe", TurnIndex = 1, MessageSequence = 1,
                ExternalUserIdentity = "user", Created = now
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = conversationId, NotebookConversation = conversation,
                Role = ChatRole.Assistant, Content = "Searching...", TurnIndex = 1, MessageSequence = 2,
                AssistantName = "Guide", IsStreaming = false, Created = now.AddSeconds(1)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = conversationId, NotebookConversation = conversation,
                Role = ChatRole.Tool, Content = "internal search result", ToolCallId = "server_search_1",
                FunctionName = "Search", TurnIndex = 1, MessageSequence = 3, Created = now.AddSeconds(2)
            },
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = conversationId, NotebookConversation = conversation,
                Role = ChatRole.Assistant, Content = finalAssistantText, TurnIndex = 1, MessageSequence = 4,
                AssistantName = "Guide", IsStreaming = false, Created = now.AddSeconds(3)
            });
        await db.SaveChangesAsync();

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "what else?" && r.ClientMessages == null),
                pubId.ToString(), "user", null, It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"More\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement($$"""
                [
                  {"role":"user","content":"describe"},
                  {"role":"assistant","content":"Searching..."},
                  {"role":"assistant","content":"{{finalAssistantText}}"},
                  {"role":"user","content":"what else?"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(s => s.CreateConversationAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task PostResponsesAsync_Continues_conversation_when_conversation_id_provided()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi", "user-a");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user-a"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "follow-up"),
                pubId.ToString(),
                "user-a",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Follow-up\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"follow-up\""),
            Conversation = ParseJsonElement($"\"conv_{conversationId:N}\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("conversation").GetString().Should().Be($"conv_{conversationId:N}");
        conversationService.Verify(s => s.CreateConversationAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task PostResponsesAsync_Returns_invalid_conversation_id_for_bad_format()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        using var db = CreateDbContext();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"hello\""),
            Conversation = ParseJsonElement("\"not-a-conversation-id\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("invalid_conversation_id");
        error.GetProperty("param").GetString().Should().Be("conversation");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Rejects_conversation_scope_mismatch()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi", "owner");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "intruder"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"hello\""),
            Conversation = ParseJsonElement($"\"conv_{conversationId:N}\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("conversation_scope_mismatch");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Rejects_conversation_and_previous_response_mismatch()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationA, now, "a", "reply a", "user-a", assistantMessageId);
        await SeedTranscriptConversationAsync(db, notebookId, conversationB, now.AddSeconds(5), "b", "reply b", "user-a");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user-a"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("\"hello\""),
            Conversation = ParseJsonElement($"\"conv_{conversationB:N}\""),
            PreviousResponseId = $"resp_{assistantMessageId:N}"
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("conversation_previous_response_mismatch");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Invalid_conversation_does_not_fall_back_to_transcript_matching()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("""
                [
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi"},
                  {"role":"user","content":"follow up"}
                ]
                """),
            Conversation = ParseJsonElement("\"conv_not_a_real_id\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        conversationService.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task PostResponsesAsync_Continues_conversation_from_manual_transcript_replay()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi there", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "follow up" && r.ClientMessages == null),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Continued\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("""
                [
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi there"},
                  {"role":"user","content":"follow up"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(s => s.CreateConversationAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public async Task PostResponsesAsync_Invalid_previous_response_id_does_not_fall_back_to_transcript()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Input = ParseJsonElement("""
                [
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi"},
                  {"role":"user","content":"follow up"}
                ]
                """),
            PreviousResponseId = "resp_not_a_real_id"
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_previous_response_id");
        conversationService.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task PostEmbeddingsAsync_Uses_service_mode_and_records_provider_metadata()
    {
        var pubId = Guid.NewGuid();
        var executionContext = CreateExecutionContext(pubId);
        var resolver = new StubResolver(executionContext);
        var embeddingService = new Mock<IEmbeddingService>();
        embeddingService
            .Setup(s => s.GetEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(),
                EmbeddingPurpose.Query,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([ [ 0.1f, 0.2f, 0.3f ] ]);

        var modeResolver = new Mock<IServiceModeResolver>();
        modeResolver
            .Setup(s => s.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceMode(
                ModeId: "emb-default",
                ProviderSection: "EmbeddingsSection",
                ModelId: "text-embedding-test",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true));

        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiEmbeddingsRequest
        {
            Model = "embeddings",
            Input = ParseJsonElement("\"hello\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostEmbeddingsAsync(
            http,
            pubId,
            request,
            resolver,
            embeddingService.Object,
            modeResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        modeResolver.Verify(
            s => s.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()),
            Times.Once);
        usageRecorder.Calls.Should().ContainSingle();
        usageRecorder.Calls[0].ProviderServiceMode.Should().Be("emb-default");
        usageRecorder.Calls[0].ProviderModel.Should().Be("text-embedding-test");
        usageRecorder.Calls[0].Service.Should().Be("EmbeddingsSection");
    }

    [TestMethod]
    public async Task PostImageGenerationsAsync_Requires_prompt()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var imageService = new Mock<INotebookImageService>(MockBehavior.Strict);
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        var storagePathResolver = new Mock<IStoragePathResolver>(MockBehavior.Strict);
        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();

        var result = await PublishedOpenAiWireHandlers.PostImageGenerationsAsync(
            http,
            pubId,
            new PublishedOpenAiWireHandlers.OpenAiImageGenerationsRequest { Model = "image", Prompt = "" },
            resolver,
            imageService.Object,
            modeResolver.Object,
            storagePathResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_prompt");
    }

    [TestMethod]
    public async Task PostAudioTranscriptionsAsync_Requires_multipart_form_data()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var transcriptionService = new Mock<ISpeechTranscriptionService>(MockBehavior.Strict);
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";

        var result = await PublishedOpenAiWireHandlers.PostAudioTranscriptionsAsync(
            http,
            pubId,
            resolver,
            transcriptionService.Object,
            modeResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_content_type");
    }

    [TestMethod]
    public async Task PostAudioSpeechAsync_Rejects_non_wav_response_format()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var speechService = new Mock<ISpeechSynthesisService>(MockBehavior.Strict);
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();

        var result = await PublishedOpenAiWireHandlers.PostAudioSpeechAsync(
            http,
            pubId,
            new PublishedOpenAiWireHandlers.OpenAiAudioSpeechRequest
            {
                Model = "speech",
                Input = "hello",
                ResponseFormat = "mp3"
            },
            resolver,
            speechService.Object,
            modeResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("unsupported_feature");
        error.GetProperty("param").GetString().Should().Be("response_format");
    }

    private static async Task SeedTranscriptConversationAsync(
        ApplicationDbContext db,
        Guid notebookId,
        Guid conversationId,
        DateTime activityTime,
        string userText,
        string assistantText,
        string externalUserIdentity,
        Guid? assistantMessageId = null)
    {
        var project = db.Projects.Local.FirstOrDefault() ?? new Project
        {
            Id = Guid.NewGuid(),
            Title = "Project",
            Slug = $"project-{notebookId:N}"
        };
        if (db.Projects.Local.All(p => p.Id != project.Id))
        {
            db.Projects.Add(project);
        }

        var notebook = db.Notebooks.Local.FirstOrDefault(n => n.Id == notebookId) ?? new Notebook
        {
            Id = notebookId,
            ProjectId = project.Id,
            Project = project,
            Title = "Notebook",
            Slug = $"notebook-{notebookId:N}"
        };
        if (db.Notebooks.Local.All(n => n.Id != notebookId))
        {
            db.Notebooks.Add(notebook);
        }

        var conversation = new NotebookConversation
        {
            Id = conversationId,
            NotebookId = notebookId,
            Notebook = notebook,
            Title = "Conversation",
            Created = activityTime
        };
        var turn = new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            NotebookConversation = conversation,
            TurnIndex = 1,
            AssistantName = "Guide",
            Instructions = userText,
            Status = "completed",
            Created = activityTime,
            LastUpdated = activityTime
        };

        db.NotebookConversations.Add(conversation);
        db.ConversationTurns.Add(turn);
        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.User,
                Content = userText,
                TurnIndex = 1,
                MessageSequence = 1,
                ExternalUserIdentity = externalUserIdentity,
                Created = activityTime
            },
            new NotebookConversationMessage
            {
                Id = assistantMessageId ?? Guid.NewGuid(),
                NotebookConversationId = conversationId,
                NotebookConversation = conversation,
                Role = ChatRole.Assistant,
                Content = assistantText,
                TurnIndex = 1,
                MessageSequence = 2,
                AssistantName = "Guide",
                IsStreaming = false,
                Created = activityTime.AddSeconds(1)
            });

        await db.SaveChangesAsync();
    }

    private static PublishedApiExecutionContext CreateExecutionContext(
        Guid pubId,
        PublishedWireApiConfigDto? wireApiConfig = null,
        Guid? notebookId = null,
        string? externalUserIdentity = "user",
        Guid? internalUserId = null)
    {
        return new PublishedApiExecutionContext(
            PubId: pubId,
            ProjectId: Guid.NewGuid(),
            NotebookId: notebookId ?? Guid.NewGuid(),
            GuideId: Guid.NewGuid(),
            PublishedGuide: new GuideAntsApi.DataModel.Models.PublishedGuide { Id = pubId, Active = true },
            WireApiConfig: wireApiConfig ?? new PublishedWireApiConfigDto { Enabled = true },
            AuthMode: PublishedApiAuthMode.Anonymous,
            ExternalUserIdentity: externalUserIdentity,
            InternalUserId: internalUserId,
            SourceChannel: PublishedApiExecutionContextResolver.WireApiSourceChannel,
            ExternalRequestId: "req-123",
            EndpointName: "models");
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"published-wire-tests-{Guid.NewGuid():N}")
            .Options;
        return new ApplicationDbContext(options);
    }

    private static JsonElement ParseJsonElement(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string ReadMessageText(AntRunner.Chat.Abstractions.ChatMessage message) =>
        string.Concat(
            (message.Content ?? Array.Empty<AntRunner.Chat.Abstractions.ChatContent>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Select(c => c.Text));

    private static async IAsyncEnumerable<StreamingEvent> StreamEvents(params StreamingEvent[] events)
    {
        foreach (var ev in events)
        {
            yield return ev;
            await Task.Yield();
        }
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return (httpContext.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class StubResolver(PublishedApiExecutionContext context) : IPublishedApiExecutionContextResolver
    {
        public Task<PublishedApiExecutionResolution> ResolveAsync(
            HttpContext httpContext,
            Guid pubId,
            string endpointName,
            int? endpointMaxBytes = null,
            bool requireWireApiEnabled = true,
            string? sourceChannel = null,
            CancellationToken ct = default)
        {
            var resolved = context with { EndpointName = endpointName };
            return Task.FromResult(PublishedApiExecutionResolution.Pass(resolved));
        }
    }

    private sealed class CapturingWireUsageRecorder : IPublishedWireUsageRecorder
    {
        public List<Call> Calls { get; } = [];

        public Task RecordAsync(
            PublishedApiExecutionContext context,
            GuideAnts.Usage.UsageCategory category,
            string service,
            string operation,
            UsageMetrics metrics,
            string endpoint,
            string status = "success",
            string? alias = null,
            string? providerModel = null,
            string? providerServiceMode = null,
            long? requestBytes = null,
            long? inputCount = null,
            long? outputCount = null,
            decimal costUsd = 0m,
            string? modelDeploymentId = null,
            CancellationToken ct = default)
        {
            Calls.Add(new Call(service, operation, endpoint, alias, providerModel, providerServiceMode, status));
            return Task.CompletedTask;
        }

        public sealed record Call(
            string Service,
            string Operation,
            string Endpoint,
            string? Alias,
            string? ProviderModel,
            string? ProviderServiceMode,
            string Status);
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Continues_conversation_from_transcript_replay_with_unpersisted_client_prefix()
    {
        var pubId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var db = CreateDbContext();
        await SeedTranscriptConversationAsync(db, notebookId, conversationId, now, "hello", "Hi from assistant", "user");

        var resolver = new StubResolver(CreateExecutionContext(pubId, notebookId: notebookId, externalUserIdentity: "user"));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        conversationService
            .Setup(s => s.SendMessageStreamAsync(
                conversationId,
                It.Is<SendMessageRequest>(r => r.Instructions == "next question"),
                pubId.ToString(),
                "user",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(StreamEvents(
                new StreamingEvent(StreamingEventTypes.AssistantMessage, "{\"content\":\"Answer\"}"),
                new StreamingEvent(StreamingEventTypes.Usage, "{\"prompt_tokens\":3,\"completion_tokens\":2}")));

        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Messages = ParseJsonElement("""
                [
                  {"role":"user","content":"<environment_context>\n\n<cwd>D:\\repos\\GuideAnts</cwd>\n\n</environment_context>"},
                  {"role":"user","content":"hello"},
                  {"role":"assistant","content":"Hi from assistant"},
                  {"role":"user","content":"next question"}
                ]
                """)
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(http, pubId, request, resolver, conversationService.Object, db);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        conversationService.Verify(s => s.CreateConversationAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }
}
