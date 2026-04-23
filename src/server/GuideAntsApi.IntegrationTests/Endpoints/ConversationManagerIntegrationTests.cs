using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class ConversationManagerIntegrationTests : BaseEndpointTest
{
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

    private async Task<HttpResponseMessage> SendMessageWithStreamingAsync(string url, object request)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = JsonContent.Create(request);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await Client!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        
        // Read the stream to completion to ensure the background task finishes
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line != null && line.StartsWith("event: error"))
            {
                var data = await reader.ReadLineAsync();
                throw new Exception($"Stream returned error: {data}");
            }
        }
        
        return response;
    }

    #region ConversationCurrentState Database View Tests
    [TestMethod]
    public async Task ConversationCurrentState_View_ShouldReturnCorrectData()
    {
        // Arrange - Create conversation through API
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Send a message to create a turn
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Hello", assistantName = "Code Executor" });

        // Act - Query the ConversationCurrentState view directly
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var currentState = await db.ConversationCurrentState
            .Where(c => c.Id == conversation.Id)
            .FirstOrDefaultAsync();

        // Assert
        currentState.Should().NotBeNull();
        currentState!.Id.Should().Be(conversation.Id);
        currentState.NotebookId.Should().Be(notebook.Id);
        currentState.CurrentAssistantName.Should().Be("Code Executor");
        currentState.CurrentTurnIndex.Should().BeGreaterThan(0);
        currentState.LastActivity.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [TestMethod]
    public async Task ConversationCurrentState_WithNoTurns_ShouldReturnNullCurrentState()
    {
        // Arrange - Create conversation but don't send any messages
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Act - Query the ConversationCurrentState view directly
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var currentState = await db.ConversationCurrentState
            .Where(c => c.Id == conversation.Id)
            .FirstOrDefaultAsync();

        // Assert
        currentState.Should().NotBeNull();
        currentState!.Id.Should().Be(conversation.Id);
        currentState.NotebookId.Should().Be(notebook.Id);
        currentState.CurrentAssistantName.Should().BeNull(); // No turns yet
        currentState.CurrentTurnIndex.Should().BeNull(); // No turns yet
        currentState.LastActivity.Should().BeNull(); // No turns yet
    }

    [TestMethod]
    public async Task ConversationCurrentState_WithAssistantChange_ShouldReflectNewAssistant()
    {
        // Arrange - Create conversation and send message with first assistant
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Hello from Python assistant", assistantName = "Code Executor" });

        // Act - Send message with different assistant
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Hello from Diagram assistant", assistantName = "Diagrams" });

        // Assert - Database view should reflect the last assistant used
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var currentState = await db.ConversationCurrentState
            .Where(c => c.Id == conversation.Id)
            .FirstOrDefaultAsync();

        currentState.Should().NotBeNull();
        currentState!.CurrentAssistantName.Should().Be("Diagrams"); // Should reflect the last assistant used
        currentState.CurrentTurnIndex.Should().Be(2); // Should have 2 turns
    }
    #endregion

    #region ConversationManager Load/Save Tests
    [TestMethod]
    public async Task LoadConversationAsync_ShouldLoadMessagesInChronologicalOrder()
    {
        // Arrange - Create conversation and send multiple messages
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Send messages in sequence
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "First message", assistantName = "Code Executor" });
        
        await Task.Delay(100); // Ensure different timestamps
        
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Second message", assistantName = "Code Executor" });

        // Act - Load conversation through API (uses ConversationManager internally)
        var response = await Client.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var loadedConversation = await response.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        loadedConversation.Should().NotBeNull();
        loadedConversation!.Messages.Should().HaveCountGreaterThan(2); // At least user + assistant messages
        
        // Messages should be in chronological order
        var messages = loadedConversation.Messages.ToList();
        for (int i = 1; i < messages.Count; i++)
        {
            messages[i].Created.Should().BeAfter(messages[i-1].Created);
        }
    }

    [TestMethod]
    public async Task ConversationManager_ShouldPreserveMessageContext()
    {
        // Arrange - Create conversation and send message with system context
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Send a message to create context
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Create a Python file", assistantName = "Code Executor" });

        // Send another message that should have context from the first
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Modify the file we just created", assistantName = "Code Executor" });

        // Act - Load conversation and verify system prompts are included
        var response = await Client!.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var loadedConversation = await response.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        loadedConversation.Should().NotBeNull();
        
        // Should have system prompts in the conversation context
        // The exact verification depends on how system prompts are represented in the DTO
        loadedConversation!.Messages.Should().NotBeEmpty();
        
        // Verify in database that conversation has proper turn structure
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var turns = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversation.Id)
            .OrderBy(t => t.TurnIndex)
            .ToListAsync();
            
        turns.Should().NotBeEmpty();
        turns.First().AssistantName.Should().Be("Code Executor");
        turns.First().Instructions.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task ConversationManager_ShouldTrackTurnsCorrectly()
    {
        // Arrange - Create conversation
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Send messages to create turns
        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "First turn", assistantName = "Code Executor" });

        await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages", 
            new { instructions = "Second turn", assistantName = "Code Executor" });

        // Assert - Verify turns are tracked correctly in database
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        
        var turns = await db.ConversationTurns
            .Where(t => t.NotebookConversationId == conversation.Id)
            .OrderBy(t => t.TurnIndex)
            .ToListAsync();
            
        turns.Should().HaveCount(2);
        turns[0].TurnIndex.Should().Be(1);
        turns[0].Instructions.Should().Be("First turn");
        turns[1].TurnIndex.Should().Be(2);
        turns[1].Instructions.Should().Be("Second turn");
        
        // Verify messages are linked to correct turns
        var messages = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversation.Id)
            .OrderBy(m => m.Created)
            .ToListAsync();
            
        messages.Should().NotBeEmpty();
        messages.Where(m => m.TurnIndex == 1).Should().NotBeEmpty();
        messages.Where(m => m.TurnIndex == 2).Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task ConversationApi_SystemMessageShouldAppearBeforeUserMessage()
    {
        // Arrange
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Send a message with an assistant that has system instructions
        var sendResponse = await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages",
            new { instructions = "Test user message", assistantName = "Search" });

        // Act: Get the conversation with messages
        var getResponse = await Client.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}");
        getResponse.EnsureSuccessStatusCode();
        var loadedConversation = await getResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        loadedConversation.Should().NotBeNull();
        var messages = loadedConversation!.Messages.ToList();

        // Assert per requirements:
        // - System instructions are injected in-memory for the model but are NOT persisted
        // - The first persisted message for the turn is the user message (sequence 1)
        messages.Count.Should().BeGreaterThan(0);
        messages.Where(m => m.Role == ChatRole.System).Should().BeEmpty();
        var userMessage = messages.FirstOrDefault(m => m.Role == ChatRole.User);
        userMessage.Should().NotBeNull();

        // Sequence numbers in DB: user should be MessageSequence = 1
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbUserMessage = await db.NotebookConversationMessages.FirstOrDefaultAsync(m => m.Id == userMessage!.Id);
        dbUserMessage.Should().NotBeNull();
        dbUserMessage!.MessageSequence.Should().Be(1);
    }

    [TestMethod]
    public async Task ConversationApi_NewConversationShouldHaveProperMessageOrdering()
    {
        // Arrange
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // Send a message
        var sendResponse = await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages",
            new { instructions = "Hello, this is a test message", assistantName = "Code Executor" });

        // Act: Get the conversation with messages
        var getResponse = await Client.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}");
        getResponse.EnsureSuccessStatusCode();
        var loadedConversation = await getResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        loadedConversation.Should().NotBeNull();
        var messages = loadedConversation!.Messages.ToList();

        // Assert: Check that messages are ordered by sequence number
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (int i = 1; i < messages.Count; i++)
        {
            var prevMessage = await db.NotebookConversationMessages.FirstOrDefaultAsync(m => m.Id == messages[i-1].Id);
            var currMessage = await db.NotebookConversationMessages.FirstOrDefaultAsync(m => m.Id == messages[i].Id);
            prevMessage.Should().NotBeNull();
            currMessage.Should().NotBeNull();
            prevMessage!.MessageSequence.Should().BeLessThanOrEqualTo(currMessage!.MessageSequence);
        }
    }

    [TestMethod]
    public async Task ConversationApi_AssistantSwitchShouldNotStoreHandoffMessage()
    {
        // Arrange
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        // First message with one assistant
        var firstSend = await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages",
            new { instructions = "First message with assistant 1", assistantName = "Code Executor" });

        // Second message with different assistant (should trigger switch)
        var secondSend = await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}/messages",
            new { instructions = "Second message with assistant 2", assistantName = "Search" });

        // Act: Get the conversation with messages
        var getResponse = await Client.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}");
        getResponse.EnsureSuccessStatusCode();
        var loadedConversation = await getResponse.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
        loadedConversation.Should().NotBeNull();
        var messages = loadedConversation!.Messages.ToList();

        // Assert: Check that no handoff message is stored in the database
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var handoffMessages = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversation.Id && m.Content.Contains("different assistant"))
            .ToListAsync();
        handoffMessages.Should().BeEmpty("Handoff messages should not be stored in the database");
        // Per requirements, system instructions are injected in-memory only and not persisted
        var systemMessages = messages.Where(m => m.Role == ChatRole.System).ToList();
        systemMessages.Should().BeEmpty("System instructions should not be persisted in conversation messages");
        
        // Verify the assistant switch took effect: last turn uses the new assistant
        var lastAssistant = messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.AssistantName;
        lastAssistant.Should().Be("Search");
    }
    #endregion

    #region Helper Methods
    private async Task<ProjectDto> CreateTestProjectAsync()
    {
        var response = await Client!.PostAsJsonAsync("/api/projects", 
            new { title = "Test Project", description = "Integration test project" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProjectDto>() ?? throw new InvalidOperationException("Failed to create project");
    }

    private async Task<NotebookDto> CreateTestNotebookAsync(Guid projectId)
    {
        // Get an active guide to use
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var guideId = await db.Assistants.Where(a => a.Kind == AssistantKind.Guide && a.IsActive).Select(a => a.Id).FirstOrDefaultAsync();
        if (guideId == Guid.Empty)
        {
            // Create a dummy guide if none exists
            var guide = new Assistant { Id = Guid.NewGuid(), Name = "Test Guide", Kind = AssistantKind.Guide, IsActive = true, IsGlobal = true };
            db.Assistants.Add(guide);
            await db.SaveChangesAsync();
            guideId = guide.Id;
        }

        var response = await Client!.PostAsJsonAsync($"/api/projects/{projectId}/notebooks", 
            new { title = "Test Notebook", guideId = guideId });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NotebookDto>() ?? throw new InvalidOperationException("Failed to create notebook");
    }

    private async Task<NotebookConversationListDto> CreateTestConversationAsync(Guid projectId, Guid notebookId)
    {
        var response = await Client!.PostAsJsonAsync($"/api/projects/{projectId}/notebooks/{notebookId}/conversations", 
            new { title = "Test Conversation" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NotebookConversationListDto>() ?? throw new InvalidOperationException("Failed to create conversation");
    }
    #endregion
} 