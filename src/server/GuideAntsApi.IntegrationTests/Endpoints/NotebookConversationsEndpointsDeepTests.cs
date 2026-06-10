using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Endpoints;

/// <summary>
/// HTTP coverage for the authenticated <c>NotebookConversationsEndpoints</c> branches that were
/// not exercised by the existing stubbed streaming tests: the non-SSE rejection, the streaming
/// not-found catch branch, message editing (404/400/success), undo (404/success), conversation
/// title generation (404), and the save-as markdown conversion path. These run against the real
/// <c>ConversationService</c> + real SQL via the integration host (fake chat client).
/// </summary>
[TestClass]
public sealed class NotebookConversationsEndpointsDeepTests : BaseEndpointTest
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

    // Other test classes may leave PublishedGuides rows that reference notebooks; remove them
    // before the base cleanup deletes notebooks to avoid FK conflicts.
    protected override async Task CleanDatabaseAsync()
    {
        if (SharedFactory != null)
        {
            using var scope = SharedFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
        }

        await base.CleanDatabaseAsync();
    }

    private sealed record Created(Guid ProjectId, Guid NotebookId, Guid ConversationId);

    private async Task<Created> CreateProjectNotebookConversationAsync()
    {
        var projResp = await Client.PostAsJsonAsync("/api/projects", new { title = $"deep-{Guid.NewGuid():N}", description = "d" });
        projResp.EnsureSuccessStatusCode();
        var project = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var nbResp = await Client.PostAsJsonAsync(
            $"/api/projects/{project!.Id}/notebooks",
            new { title = $"nb-{Guid.NewGuid():N}", guideId = await GetDefaultGuideIdAsync() });
        nbResp.EnsureSuccessStatusCode();
        var notebook = await nbResp.Content.ReadFromJsonAsync<NotebookDto>();

        var convResp = await Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/notebooks/{notebook!.Id}/conversations",
            new { title = "Deep Test" });
        convResp.EnsureSuccessStatusCode();
        var conversation = await convResp.Content.ReadFromJsonAsync<NotebookConversationListDto>();

        return new Created(project.Id, notebook.Id, conversation!.Id);
    }

    private async Task SeedTurnWithMessagesAsync(
        Guid conversationId,
        string userContent,
        string assistantContent)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ConversationTurns.Add(new ConversationTurn
        {
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            AssistantName = "assistant",
            Instructions = userContent,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow,
            Status = "completed"
        });

        db.NotebookConversationMessages.AddRange(
            new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = DataModelChatRole.User,
                Content = userContent,
                Created = DateTime.UtcNow
            },
            new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 2,
                Role = DataModelChatRole.Assistant,
                AssistantName = "assistant",
                Content = assistantContent,
                Created = DateTime.UtcNow.AddSeconds(1)
            });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> GetMessageIdAsync(Guid conversationId, DataModelChatRole role)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == role)
            .Select(m => m.Id)
            .FirstAsync();
    }

    [TestMethod]
    public async Task SendMessage_WithoutEventStreamAccept_Returns400()
    {
        var created = await CreateProjectNotebookConversationAsync();

        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages")
        {
            Content = JsonContent.Create(new { instructions = "Hello" })
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var resp = await Client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Contain("text/event-stream");
    }

    [TestMethod]
    public async Task SendMessage_Stream_ForMissingConversation_Returns404()
    {
        var created = await CreateProjectNotebookConversationAsync();
        var missingConversationId = Guid.NewGuid();

        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{missingConversationId}/messages")
        {
            Content = JsonContent.Create(new { instructions = "Hello" })
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetString().Should().Contain("Conversation not found");
    }

    [TestMethod]
    public async Task EditMessage_ForMissingMessage_Returns404()
    {
        var created = await CreateProjectNotebookConversationAsync();

        var resp = await Client.PatchAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages/{Guid.NewGuid()}",
            JsonContent.Create(new { content = "updated" }));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task EditMessage_ForUserMessage_Returns400()
    {
        var created = await CreateProjectNotebookConversationAsync();
        await SeedTurnWithMessagesAsync(created.ConversationId, "user question", "assistant answer");
        var userMessageId = await GetMessageIdAsync(created.ConversationId, DataModelChatRole.User);

        var resp = await Client.PatchAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages/{userMessageId}",
            JsonContent.Create(new { content = "cannot edit user" }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task EditMessage_ForAssistantMessage_Succeeds()
    {
        var created = await CreateProjectNotebookConversationAsync();
        await SeedTurnWithMessagesAsync(created.ConversationId, "user question", "assistant answer");
        var assistantMessageId = await GetMessageIdAsync(created.ConversationId, DataModelChatRole.Assistant);

        var resp = await Client.PatchAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages/{assistantMessageId}",
            JsonContent.Create(new { content = "edited assistant answer" }));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.NotebookConversationMessages.SingleAsync(m => m.Id == assistantMessageId);
        stored.Content.Should().Be("edited assistant answer");
        stored.IsEdited.Should().BeTrue();
    }

    [TestMethod]
    public async Task UndoLast_ForMissingConversation_Returns404()
    {
        var created = await CreateProjectNotebookConversationAsync();

        var resp = await Client.DeleteAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{Guid.NewGuid()}/messages/last");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task UndoLast_RemovesLatestTurn()
    {
        var created = await CreateProjectNotebookConversationAsync();
        await SeedTurnWithMessagesAsync(created.ConversationId, "user question", "assistant answer");

        var resp = await Client.DeleteAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages/last");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.NotebookConversationMessages.CountAsync(m => m.NotebookConversationId == created.ConversationId))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task UndoSpecific_ForMissingMessage_Returns404()
    {
        var created = await CreateProjectNotebookConversationAsync();
        await SeedTurnWithMessagesAsync(created.ConversationId, "user question", "assistant answer");

        var resp = await Client.DeleteAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages/{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task UndoSpecific_RemovesFromTargetTurn()
    {
        var created = await CreateProjectNotebookConversationAsync();
        await SeedTurnWithMessagesAsync(created.ConversationId, "user question", "assistant answer");
        var userMessageId = await GetMessageIdAsync(created.ConversationId, DataModelChatRole.User);

        var resp = await Client.DeleteAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/messages/{userMessageId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.NotebookConversationMessages.CountAsync(m => m.NotebookConversationId == created.ConversationId))
            .Should().Be(0);
    }

    [TestMethod]
    public async Task GenerateTitle_ForMissingConversation_Returns404()
    {
        var created = await CreateProjectNotebookConversationAsync();

        var resp = await Client.PostAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{Guid.NewGuid()}/title/generate",
            content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task SaveAs_ForMissingConversation_Returns404()
    {
        var created = await CreateProjectNotebookConversationAsync();

        var resp = await Client.PostAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{Guid.NewGuid()}/save-as",
            content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task SaveAs_CreatesMarkdownFile_FromConversation()
    {
        var created = await CreateProjectNotebookConversationAsync();
        // Assistant content includes a relative image link to exercise the path-adjustment helper.
        await SeedTurnWithMessagesAsync(
            created.ConversationId,
            "Plot something for me",
            "Here is your chart ![chart](./Output/chart.png)");

        var resp = await Client.PostAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}/save-as",
            content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var fileDto = await resp.Content.ReadFromJsonAsync<NotebookFileDto>();
        fileDto!.RelativePath.Should().StartWith("conversations/");
        fileDto.RelativePath.Should().EndWith(".md");

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await db.NotebookFiles
            .SingleAsync(f => f.NotebookId == created.NotebookId && f.RelativePath == fileDto.RelativePath);
        stored.FileSize.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task GetConversation_ForMissing_Returns404_and_RenameDelete_Succeed()
    {
        var created = await CreateProjectNotebookConversationAsync();

        var missing = await Client.GetAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{Guid.NewGuid()}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rename = await Client.PutAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}",
            JsonContent.Create(new { title = "Renamed Deep" }));
        rename.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await Client.GetAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/");
        list.EnsureSuccessStatusCode();

        var delete = await Client.DeleteAsync(
            $"/api/projects/{created.ProjectId}/notebooks/{created.NotebookId}/conversations/{created.ConversationId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.NotebookConversations.AnyAsync(c => c.Id == created.ConversationId)).Should().BeFalse();
    }
}
