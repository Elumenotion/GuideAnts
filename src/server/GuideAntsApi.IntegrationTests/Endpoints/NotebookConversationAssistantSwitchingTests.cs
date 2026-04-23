using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Text.Json;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.IntegrationTests.Infrastructure;
using FluentAssertions;
using System.Net.Http.Json;
using GuideAntsApi.Services.Conversations;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Runtime.CompilerServices;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class NotebookConversationAssistantSwitchingTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _projectId;
    private Guid _notebookId;
    private Guid _conversationId;

    private class MockConversationService : IConversationService
    {
        private readonly List<NotebookConversationListDto> _conversations = new();
        private readonly Dictionary<Guid, NotebookConversationWithMessagesDto> _conversationsWithMessages = new();
        private readonly List<StreamingEvent> _streamingEvents = new();

        public Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId) => throw new NotImplementedException();

        public Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId)
        {
            // If conversation doesn't exist, create a basic one
            if (!_conversationsWithMessages.ContainsKey(conversationId))
            {
                _conversationsWithMessages[conversationId] = new NotebookConversationWithMessagesDto(
                    conversationId,
                    "Test Conversation",
                    null, // No assistant initially
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    new List<MessageDto>()
                );
            }
            
            return Task.FromResult(_conversationsWithMessages.GetValueOrDefault(conversationId));
        }

        public Task<IReadOnlyList<NotebookConversationListDto>> GetListAsync(Guid notebookId)
        {
            return Task.FromResult<IReadOnlyList<NotebookConversationListDto>>(_conversations);
        }

        public Task<NotebookConversationListDto> CreateConversationAsync(Guid notebookId, string title)
        {
            var now = DateTime.UtcNow;
            var dto = new NotebookConversationListDto(Guid.NewGuid(), title, now, now);
            _conversations.Add(dto);
            return Task.FromResult(dto);
        }

        public Task RenameConversationAsync(Guid conversationId, string newTitle) => throw new NotImplementedException();
        public Task DeleteConversationAsync(Guid conversationId) => throw new NotImplementedException();
        public Task EditMessageAsync(Guid messageId, string newContent) => throw new NotImplementedException();



        public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(
            Guid conversationId, 
            SendMessageRequest request, 
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Simulate assistant switching logic
            var isAssistantSwitch = IsAssistantSwitch(conversationId, request.AssistantName);
            
            // Create messages based on whether this is an assistant switch
            var messages = new List<MessageDto>();
            
            if (isAssistantSwitch)
            {
                // Add system messages for assistant switch
                messages.Add(new MessageDto(
                    Guid.NewGuid(),
                    ChatRole.System,
                    $"Instructions for {request.AssistantName}: You are a specialized assistant.",
                    null,
                    request.AssistantName,
                    false,
                    null,
                    DateTime.UtcNow,
                    null
                ));
                
                messages.Add(new MessageDto(
                    Guid.NewGuid(),
                    ChatRole.System,
                    "The previous messages are a conversation between the user and a different assistant. Use them to understand the context of the conversation.",
                    null,
                    request.AssistantName,
                    false,
                    null,
                    DateTime.UtcNow,
                    null
                ));
            }

            // Add user message
            var userMessage = new MessageDto(
                Guid.NewGuid(),
                ChatRole.User,
                request.Instructions,
                Guid.NewGuid(),
                "user",
                false,
                null,
                DateTime.UtcNow,
                null
            );
            messages.Add(userMessage);

            // Add assistant response
            var assistantMessage = new MessageDto(
                Guid.NewGuid(),
                ChatRole.Assistant,
                $"Response from {request.AssistantName}: {request.Instructions}",
                null,
                request.AssistantName,
                false,
                null,
                DateTime.UtcNow,
                null
            );
            messages.Add(assistantMessage);

            // Update the conversation by accumulating messages
            var existingConversation = _conversationsWithMessages.GetValueOrDefault(conversationId);
            var allMessages = existingConversation?.Messages.ToList() ?? new List<MessageDto>();
            
            // Add new messages (user + assistant, system messages only on assistant switch)
            if (isAssistantSwitch)
            {
                allMessages.AddRange(messages); // Includes system messages + user + assistant
            }
            else
            {
                // Only add user and assistant messages for same assistant continuation
                allMessages.Add(userMessage);
                allMessages.Add(assistantMessage);
            }
            
            var updatedConversation = new NotebookConversationWithMessagesDto(
                conversationId,
                "Test Conversation",
                request.AssistantName, // Current assistant
                DateTime.UtcNow,
                DateTime.UtcNow,
                allMessages
            );
            _conversationsWithMessages[conversationId] = updatedConversation;

            // Stream the assistant response
            yield return new StreamingEvent("token", JsonSerializer.Serialize(new { role = "assistant", contentDelta = "Response " }));
            await Task.Delay(10, cancellationToken);
            yield return new StreamingEvent("token", JsonSerializer.Serialize(new { role = "assistant", contentDelta = "from " }));
            await Task.Delay(10, cancellationToken);
            yield return new StreamingEvent("token", JsonSerializer.Serialize(new { role = "assistant", contentDelta = request.AssistantName }));
            await Task.Delay(10, cancellationToken);
            yield return new StreamingEvent("message", JsonSerializer.Serialize(new { role = "assistant", content = assistantMessage.Content }));
            yield return new StreamingEvent("usage", JsonSerializer.Serialize(new { promptTokens = 10, completionTokens = 20, totalTokens = 30 }));
            yield return new StreamingEvent("complete", "{}");
        }

        private bool IsAssistantSwitch(Guid conversationId, string? requestedAssistant)
        {
            if (string.IsNullOrEmpty(requestedAssistant)) return false;
            
            var conversation = _conversationsWithMessages.GetValueOrDefault(conversationId);
            if (conversation == null) return false;

            return conversation.AssistantName != requestedAssistant;
        }

        public Task UndoLastForConversationAsync(Guid conversationId) => throw new NotImplementedException();
        public Task UndoForConversationAsync(Guid conversationId, Guid messageId) => throw new NotImplementedException();
        public Task<PagedUserConversationsDto> GetUserConversationsAsync(UserConversationsQuery query)
        {
            return Task.FromResult(new PagedUserConversationsDto(
                Items: Array.Empty<UserConversationDto>(),
                TotalCount: 0,
                Page: query.Page,
                PageSize: query.PageSize,
                TotalPages: 0
            ));
        }
    }

    [ClassInitialize]
    public static async Task ClassInit(TestContext ctx)
    {
        var baseFactory = new TestWebApplicationFactory();
        await baseFactory.InitializeAsync();
        _factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConversationService>();
                services.AddSingleton<IConversationService, MockConversationService>();
            });
        });
    }

    [TestInitialize]
    public async Task TestInit()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test_token");

        // Create real entities so endpoint pre-checks (e.g. runtime lookup by notebookId) succeed.
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { title = $"switch-test-{Guid.NewGuid():N}", description = "assistant-switching test" });
        projectResp.EnsureSuccessStatusCode();
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectDto>();
        _projectId = project!.Id;

        var notebookResp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/notebooks", new { title = $"switch-nb-{Guid.NewGuid():N}", guideId = await GetDefaultGuideIdAsync() });
        notebookResp.EnsureSuccessStatusCode();
        var notebook = await notebookResp.Content.ReadFromJsonAsync<NotebookDto>();
        _notebookId = notebook!.Id;

        var convResp = await _client.PostAsJsonAsync($"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations", new { title = "switch-conversation" });
        convResp.EnsureSuccessStatusCode();
        var conversation = await convResp.Content.ReadFromJsonAsync<NotebookConversationListDto>();
        _conversationId = conversation!.Id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
    }

    private async Task<Guid> GetDefaultGuideIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<GuideAntsApi.DataModel.ApplicationDbContext>(scope.ServiceProvider);
        var guideId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            System.Linq.Queryable.Select(
                System.Linq.Queryable.Where(db.Assistants, a => a.Kind == GuideAntsApi.DataModel.Models.AssistantKind.Guide && a.IsActive),
                a => a.Id));
        
        if (guideId == Guid.Empty)
        {
            var guide = new GuideAntsApi.DataModel.Models.Assistant { Id = Guid.NewGuid(), Name = "Test Guide", Kind = GuideAntsApi.DataModel.Models.AssistantKind.Guide, IsActive = true, IsGlobal = true };
            db.Assistants.Add(guide);
            await db.SaveChangesAsync();
            guideId = guide.Id;
        }
        return guideId;
    }

    [TestMethod]
    public async Task SendMessage_NewConversation_CreatesConversationWithSelectedAssistant()
    {
        // Arrange
        var request = new SendMessageRequest
        {
            Instructions = "Hello, I need help with Python",
            AssistantName = "python-assistant"
        };

        // Act
        var reqMsg1 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(request)
        };
        reqMsg1.Headers.Accept.Clear();
        reqMsg1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(reqMsg1, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        
        // Verify the conversation was updated with the correct assistant
        var conversationResponse = await _client.GetAsync(
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}");
        conversationResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        conversation.Should().NotBeNull();
        conversation!.AssistantName.Should().Be("python-assistant");
        conversation.Messages.Should().HaveCountGreaterThan(0);
    }

    [TestMethod]
    public async Task SendMessage_AssistantSwitch_AddsSystemMessagesAndPreservesContext()
    {
        // Arrange - Create initial conversation with default assistant
        var initialRequest = new SendMessageRequest
        {
            Instructions = "Hello, what can you help me with?",
            AssistantName = "assistant"
        };

        var initReq = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(initialRequest)
        };
        initReq.Headers.Accept.Clear();
        initReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await _client.SendAsync(initReq, HttpCompletionOption.ResponseHeadersRead);

        // Act - Switch to Python assistant
        var switchRequest = new SendMessageRequest
        {
            Instructions = "Help me write a Python script",
            AssistantName = "python-assistant"
        };

        var switchReq = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(switchRequest)
        };
        switchReq.Headers.Accept.Clear();
        switchReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(switchReq, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        
        // Verify the conversation was updated with the new assistant
        var conversationResponse = await _client.GetAsync(
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}");
        
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        conversation.Should().NotBeNull();
        conversation!.AssistantName.Should().Be("python-assistant");
        
        // Verify system messages were added for assistant switch
        var systemMessages = conversation.Messages.Where(m => m.Role == ChatRole.System).ToList();
        systemMessages.Should().HaveCountGreaterThan(1);
        systemMessages.Should().Contain(m => m.Content.Contains("python-assistant"));
        systemMessages.Should().Contain(m => m.Content.Contains("previous messages are a conversation"));
    }

    [TestMethod]
    public async Task SendMessage_StreamingWithAssistantSwitch_ReturnsCorrectEvents()
    {
        // Arrange - Create initial conversation
        var initialRequest = new SendMessageRequest
        {
            Instructions = "Hello",
            AssistantName = "assistant"
        };

        var initReq2 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(initialRequest)
        };
        initReq2.Headers.Accept.Clear();
        initReq2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await _client.SendAsync(initReq2, HttpCompletionOption.ResponseHeadersRead);

        // Act - Switch assistant with streaming
        var switchRequest = new SendMessageRequest
        {
            Instructions = "Write Python code",
            AssistantName = "python-assistant"
        };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(switchRequest),
            Headers = { { "Accept", "text/event-stream" } }
        };

        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var events = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (line.StartsWith("data: "))
            {
                events.Add(line.Substring(6)); // Remove "data: " prefix
            }
        }

        // Verify streaming events
        events.Should().NotBeEmpty();
        events.Should().Contain(e => e.Contains("python-assistant")); // Check that assistant name appears in content
        events.Should().Contain(e => e.Contains("content")); // Check for final message event (contains "content" field)
        events.Should().Contain(e => e.Contains("promptTokens")); // Check for usage event
        events.Should().Contain(e => e.Contains("{}")); // Check for complete event
    }

    [TestMethod]
    public async Task SendMessage_SameAssistantContinuation_DoesNotAddSystemMessages()
    {
        // Arrange - Create initial conversation
        var initialRequest = new SendMessageRequest
        {
            Instructions = "Hello",
            AssistantName = "python-assistant"
        };

        await _client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages",
            initialRequest);

        // Act - Continue with same assistant
        var continueRequest = new SendMessageRequest
        {
            Instructions = "Can you help me more?",
            AssistantName = "python-assistant"
        };

        var contReq = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(continueRequest)
        };
        contReq.Headers.Accept.Clear();
        contReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(contReq, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        
        // Verify the conversation maintained the same assistant
        var conversationResponse = await _client.GetAsync(
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}");
        
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        conversation.Should().NotBeNull();
        conversation!.AssistantName.Should().Be("python-assistant");
        
        // Verify no additional system messages were added (only user and assistant messages for the follow-up)
        var lastTwoMessages = conversation.Messages.TakeLast(2).ToList();
        lastTwoMessages.Should().HaveCount(2);
        lastTwoMessages[0].Role.Should().Be(ChatRole.User);
        lastTwoMessages[1].Role.Should().Be(ChatRole.Assistant);
    }

    [TestMethod]
    public async Task SendMessage_MultipleAssistantSwitches_MaintainsCorrectContext()
    {
        // Arrange & Act - Create a conversation with multiple assistant switches
        var requests = new[]
        {
            new { Instructions = "Hello", Assistant = "assistant" },
            new { Instructions = "Write Python code", Assistant = "python-assistant" },
            new { Instructions = "Create a diagram", Assistant = "diagram-assistant" },
            new { Instructions = "Help with more Python", Assistant = "python-assistant" }
        };

        foreach (var req in requests)
        {
            var request = new SendMessageRequest
            {
                Instructions = req.Instructions,
                AssistantName = req.Assistant
            };

            var loopReq = new HttpRequestMessage(HttpMethod.Post,
                $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
            {
                Content = JsonContent.Create(request)
            };
            loopReq.Headers.Accept.Clear();
            loopReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await _client.SendAsync(loopReq, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }

        // Assert - Verify final state
        var conversationResponse = await _client.GetAsync(
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}");
        
        var conversation = await conversationResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        conversation.Should().NotBeNull();
        
        // Final assistant should be python-assistant
        conversation!.AssistantName.Should().Be("python-assistant");
        
        // Should have messages from all interactions
        conversation.Messages.Should().HaveCountGreaterThan(8); // At least 2 messages per request
        
        // Should have system messages for assistant switches
        var systemMessages = conversation.Messages.Where(m => m.Role == ChatRole.System).ToList();
        systemMessages.Should().HaveCountGreaterThan(0);
    }

    [TestMethod]
    public async Task GetConversation_AfterAssistantSwitch_ReturnsCurrentAssistant()
    {
        // Arrange - Create conversation and switch assistant
        var initialRequest = new SendMessageRequest
        {
            Instructions = "Hello",
            AssistantName = "assistant"
        };

        var initReq3 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(initialRequest)
        };
        initReq3.Headers.Accept.Clear();
        initReq3.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await _client.SendAsync(initReq3, HttpCompletionOption.ResponseHeadersRead);

        var switchRequest = new SendMessageRequest
        {
            Instructions = "Help with Python",
            AssistantName = "python-assistant"
        };

        var switchReq2 = new HttpRequestMessage(HttpMethod.Post,
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}/messages")
        {
            Content = JsonContent.Create(switchRequest)
        };
        switchReq2.Headers.Accept.Clear();
        switchReq2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await _client.SendAsync(switchReq2, HttpCompletionOption.ResponseHeadersRead);

        // Act
        var response = await _client.GetAsync(
            $"/api/projects/{_projectId}/notebooks/{_notebookId}/conversations/{_conversationId}");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        
        var conversation = await response.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        conversation.Should().NotBeNull();
        conversation!.AssistantName.Should().Be("python-assistant");
    }
} 
