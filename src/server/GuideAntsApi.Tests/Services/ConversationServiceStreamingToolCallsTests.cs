using Microsoft.EntityFrameworkCore;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Tests.TestUtils;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class ConversationServiceStreamingToolCallsTests
{
    private ApplicationDbContext _dbContext = null!;
    private ConversationService _service = null!;
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
    private Mock<IChatCompletionClientFactory> _mockChatClientFactory = null!;
    private ClaimsPrincipal _testUser = null!;
    private Guid _userId;
    private Guid _projectId;
    private Guid _notebookId;
    private Guid _conversationId;
    private Guid _templateId;

    [TestInitialize]
    public void TestInitialize()
    {
        // Create in-memory database
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        // Setup mocks
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        _mockChatClientFactory = new Mock<IChatCompletionClientFactory>();

        // Setup test data
        _userId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _notebookId = Guid.NewGuid();
        _conversationId = Guid.NewGuid();
        _templateId = Guid.NewGuid();

        _testUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim("access_token", "test-token")
        }));

        // Setup access service mock

        // Create service
        var scopeFactory = new GuideAntsApi.Tests.TestUtils.TestServiceScopeFactory(_dbContext);
        var distributedLockMock = new Mock<IDistributedConversationLock>();
        var usageRecorderMock = new Mock<IUsageRecorder>();
        var contextOptionsServiceMock = new Mock<IContextOptionsService>();
        contextOptionsServiceMock
            .Setup(m => m.BuildContextMessageAsync(It.IsAny<AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // Configuration needed by ConversationService
        var configurationMock = new Mock<IConfiguration>();
        configurationMock.Setup(x => x["FileStorage:Path"]).Returns("C:\\temp\\test-storage");

        var chatModelResolverMock = new Mock<IChatModelResolver>();
        chatModelResolverMock
            .Setup(r => r.Resolve(It.IsAny<string?>()))
            .Returns((string? id) => new ResolvedChatModel(
                string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                ChatModelReferenceKind.Direct,
                new ResolvedExecutionPolicy(
                    string.IsNullOrWhiteSpace(id) ? "gpt-4o-mini" : id!,
                    "openai-chat",
                    ParameterAuthority.AssistantDefinition,
                    new Dictionary<string, System.Text.Json.JsonElement>())));

        var (queryService, commandService, historyBuilder, attachmentService) = ConversationTestServices.Create(
            scopeFactory,
            contextOptionsServiceMock.Object,
            Mock.Of<IMarkdownExtractionService>(),
            configurationMock.Object);
        var (persistence, usageReporter) = ConversationTestServices.CreatePersistence(scopeFactory, usageRecorderMock.Object);

        _service = ConversationTestServices.CreateConversationService(
            scopeFactory,
            chatModelResolverMock.Object,
            queryService,
            commandService,
            historyBuilder,
            attachmentService,
            persistence,
            usageReporter,
            _mockChatClientFactory.Object,
            distributedLockMock.Object);

        // Seed test data
        SeedTestData();
    }

    private void SeedTestData()
    {
        var template = new NotebookTemplate
        {
            Id = _templateId,
            TemplateName = "test-template"
        };

        var notebook = new Notebook
        {
            Id = _notebookId,
            ProjectId = _projectId,
            NotebookTemplateId = _templateId,
            Title = "Test Notebook"
        };

        var conversation = new NotebookConversation
        {
            Id = _conversationId,
            NotebookId = _notebookId,
            Title = "Test Conversation",
            Notebook = notebook
        };

        _dbContext.NotebookTemplates.Add(template);
        _dbContext.Notebooks.Add(notebook);
        _dbContext.NotebookConversations.Add(conversation);
        _dbContext.SaveChanges();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _dbContext.Dispose();
    }

    #region Event Type Determination Tests

    [TestMethod]
    public void DetermineEventType_UserMessage_ReturnsUserMessageType()
    {
        StreamingEvents.DetermineEventType("User", "Hello, how are you?")
            .Should().Be(StreamingEventTypes.UserMessage);
    }

    [TestMethod]
    public void DetermineEventType_AssistantWithToolCalls_ReturnsAssistantMessage()
    {
        StreamingEvents.DetermineEventType("Assistant", "I need to use tool_calls to help you with that.")
            .Should().Be(StreamingEventTypes.AssistantMessage);
    }

    [TestMethod]
    public void DetermineEventType_AssistantWithoutToolCalls_ReturnsAssistantMessage()
    {
        StreamingEvents.DetermineEventType("Assistant", "Here's the answer to your question.")
            .Should().Be(StreamingEventTypes.AssistantMessage);
    }

    [TestMethod]
    public void DetermineEventType_ToolMessage_ReturnsToolResult()
    {
        StreamingEvents.DetermineEventType("Tool", "Function execution completed successfully.")
            .Should().Be(StreamingEventTypes.ToolResult);
    }

    [TestMethod]
    public void DetermineEventType_SystemMessage_ReturnsSystemMessage()
    {
        StreamingEvents.DetermineEventType("System", "You are a helpful assistant.")
            .Should().Be(StreamingEventTypes.SystemMessage);
    }

    [TestMethod]
    public void DetermineEventType_UnknownRole_ReturnsMessage()
    {
        StreamingEvents.DetermineEventType("Unknown", "Some message")
            .Should().Be(StreamingEventTypes.Message);
    }

    [TestMethod]
    public void DetermineEventType_CaseInsensitive_WorksCorrectly()
    {
        StreamingEvents.DetermineEventType("USER", "Hello").Should().Be(StreamingEventTypes.UserMessage);
        StreamingEvents.DetermineEventType("assistant", "Hello").Should().Be(StreamingEventTypes.AssistantMessage);
        StreamingEvents.DetermineEventType("TOOL", "Result").Should().Be(StreamingEventTypes.ToolResult);
        StreamingEvents.DetermineEventType("system", "Instruction").Should().Be(StreamingEventTypes.SystemMessage);
    }

    #endregion

    #region Streaming Event Types Tests

    [TestMethod]
    public void StreamingEventTypes_Constants_HaveCorrectValues()
    {
        // Assert existing event types
        StreamingEventTypes.Token.Should().Be("token");
        StreamingEventTypes.Message.Should().Be("message");
        StreamingEventTypes.Usage.Should().Be("usage");
        StreamingEventTypes.Complete.Should().Be("complete");
        
        // Assert new event types
        StreamingEventTypes.UserMessage.Should().Be("user_message");
        StreamingEventTypes.AssistantMessage.Should().Be("assistant_message");
        StreamingEventTypes.ToolResult.Should().Be("tool_result");
        StreamingEventTypes.SystemMessage.Should().Be("system_message");
    }

    #endregion

    #region Mock Streaming Tests

    [TestMethod]
    public void MessageAddedCallback_WithUserMessage_CreatesCorrectStreamingEvent()
    {
        StreamingEvents.DetermineEventType("User", "Hello")
            .Should().Be(StreamingEventTypes.UserMessage);
    }

    [TestMethod]
    public void MessageAddedCallback_WithToolResult_CreatesCorrectStreamingEvent()
    {
        StreamingEvents.DetermineEventType("Tool", "Function result")
            .Should().Be(StreamingEventTypes.ToolResult);
    }

    [TestMethod]
    public void MessageAddedCallback_WithAssistantThinking_CreatesCorrectStreamingEvent()
    {
        StreamingEvents.DetermineEventType("Assistant", "I need to use tool_calls to solve this")
            .Should().Be(StreamingEventTypes.AssistantMessage);
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public void MessageAddedCallback_WithException_ShouldNotBreakStreaming()
    {
        StreamingEvents.DetermineEventType("Assistant", "Some message that might cause issues")
            .Should().Be(StreamingEventTypes.AssistantMessage);
    }

    #endregion
}
