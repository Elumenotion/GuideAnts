using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.Options;
using Moq;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public class ConversationServiceTests
{
    private ApplicationDbContext _db = null!;
    private HttpClient _httpClient = null!;
    private ConversationService _service = null!;

    [TestInitialize]
    public void Init()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);
        
        // Mock IHttpClientFactory for tests
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(mockHttpMessageHandler.Object);
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_httpClient);
        
        
        var turnManagerMock = new Mock<ITurnManager>();
        var messageManagerMock = new Mock<IMessageManager>();
        var broadcastHubMock = new Mock<IConversationBroadcastHub>();
        var markdownExtractionServiceMock = new Mock<IMarkdownExtractionService>();
        var chatClientFactoryMock = new Mock<IChatCompletionClientFactory>();
        
        var scopeFactory = new GuideAntsApi.Tests.TestUtils.TestServiceScopeFactory(_db);
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
                null,
                null,
                null));

        _service = new ConversationService(
            mockHttpClientFactory.Object,
            turnManagerMock.Object,
            broadcastHubMock.Object,
            distributedLockMock.Object,
            scopeFactory,
            usageRecorderMock.Object,
            chatClientFactoryMock.Object,
            contextOptionsServiceMock.Object,
            Microsoft.Extensions.Options.Options.Create(new MarkdownAttachmentOptions()),
            chatModelResolverMock.Object,
            null,
            null,
            markdownExtractionServiceMock.Object,
            Mock.Of<ILogger<ConversationService>>(),
            configurationMock.Object
        );

        // Seed notebook
        var project = new Project { Id = Guid.NewGuid(), Title = "Proj" };
        var notebook = new Notebook { Id = Guid.NewGuid(), Title = "NB", Project = project };
        _db.Projects.Add(project);
        _db.Notebooks.Add(notebook);
        _db.SaveChanges();
    }

    [TestMethod]
    public async Task CreateConversation_CreatesEntity()
    {
        var nbId = _db.Notebooks.First().Id;
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString())
        }));
        var dto = await _service.CreateConversationAsync(nbId, "Test Conversation");
        dto.Should().NotBeNull();
        dto.Title.Should().Be("Test Conversation");
        (await _db.NotebookConversations.CountAsync()).Should().Be(1);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _httpClient.Dispose();
    }
} 