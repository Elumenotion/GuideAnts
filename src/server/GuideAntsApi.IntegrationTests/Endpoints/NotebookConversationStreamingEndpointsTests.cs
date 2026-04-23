using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FluentAssertions;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Net;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class NotebookConversationStreamingEndpointsTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private class StubConversationService : IConversationService
    {
        private readonly List<NotebookConversationListDto> _conversations = new();
        
        public Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId) => throw new NotImplementedException();
        public Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId) => throw new NotImplementedException();
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


        
        public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(Guid conversationId, SendMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamingEvent("token", JsonSerializer.Serialize(new { role = "assistant", contentDelta = "Hel" }));
            await Task.Delay(10, cancellationToken);
            yield return new StreamingEvent("token", JsonSerializer.Serialize(new { role = "assistant", contentDelta = "lo" }));
            await Task.Delay(10, cancellationToken);
            yield return new StreamingEvent("message", JsonSerializer.Serialize(new { role = "assistant", content = "Hello" }));
            yield return new StreamingEvent("usage", JsonSerializer.Serialize(new { promptTokens = 1, completionTokens = 1, totalTokens = 2 }));
            yield return new StreamingEvent("complete", JsonSerializer.Serialize(new { }));
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

    private class LockedConversationService : IConversationService
    {
        private readonly List<NotebookConversationListDto> _conversations = new();

        public Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId) => throw new NotImplementedException();
        public Task<NotebookConversationWithMessagesDto?> GetConversationWithMessagesAsync(Guid conversationId) => throw new NotImplementedException();
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
        public async IAsyncEnumerable<StreamingEvent> SendMessageStreamToConversationAsync(Guid conversationId, SendMessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Conversation is locked by User");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
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
                services.AddSingleton<IConversationService, StubConversationService>();
            });
        });
    }

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test_token");
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
    public async Task SendMessage_Stream_ReturnsSseEventsInOrder()
    {
        // Arrange: create project + notebook via helper endpoint
        var createProjectResp = await _client.PostAsJsonAsync("/api/projects", new { title = "proj", description = "d" });
        createProjectResp.EnsureSuccessStatusCode();
        var project = await createProjectResp.Content.ReadFromJsonAsync<ProjectDto>();

        var notebookResp = await _client.PostAsJsonAsync($"/api/projects/{project!.Id}/notebooks", new { title = "nb", guideId = await GetDefaultGuideIdAsync() });
        notebookResp.EnsureSuccessStatusCode();
        var notebook = await notebookResp.Content.ReadFromJsonAsync<NotebookDto>();

        var createConvResp = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/notebooks/{notebook!.Id}/conversations", new { title = "Test" });
        createConvResp.EnsureSuccessStatusCode();
        var conversation = await createConvResp.Content.ReadFromJsonAsync<NotebookConversationListDto>();

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation!.Id}/messages");
        req.Content = JsonContent.Create(new { instructions = "Hello" });
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Act
        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        resp.EnsureSuccessStatusCode();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = new List<string>();
        while (!reader.EndOfStream && events.Count < 5)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            if (line.StartsWith("event:"))
            {
                var evtName = line.Substring("event:".Length).Trim();
                events.Add(evtName);
            }
        }

        events.Should().Equal(new[] { "token", "token", "message", "usage", "complete" });
    }

    [TestMethod]
    public async Task SendMessage_Stream_WhenConversationIsLocked_Returns409Conflict()
    {
        await using var lockedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConversationService>();
                services.AddSingleton<IConversationService, LockedConversationService>();
            });
        });

        using var lockedClient = lockedFactory.CreateClient();
        lockedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test_token");

        var createProjectResp = await lockedClient.PostAsJsonAsync("/api/projects", new { title = "proj-locked", description = "d" });
        createProjectResp.EnsureSuccessStatusCode();
        var project = await createProjectResp.Content.ReadFromJsonAsync<ProjectDto>();

        var notebookResp = await lockedClient.PostAsJsonAsync($"/api/projects/{project!.Id}/notebooks", new { title = "nb-locked", guideId = await GetDefaultGuideIdAsync() });
        notebookResp.EnsureSuccessStatusCode();
        var notebook = await notebookResp.Content.ReadFromJsonAsync<NotebookDto>();

        var createConvResp = await lockedClient.PostAsJsonAsync($"/api/projects/{project.Id}/notebooks/{notebook!.Id}/conversations", new { title = "Locked Test" });
        createConvResp.EnsureSuccessStatusCode();
        var conversation = await createConvResp.Content.ReadFromJsonAsync<NotebookConversationListDto>();

        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation!.Id}/messages");
        req.Content = JsonContent.Create(new { instructions = "Hello" });
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var resp = await lockedClient.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Contain("Conversation is locked by");
    }
} 
