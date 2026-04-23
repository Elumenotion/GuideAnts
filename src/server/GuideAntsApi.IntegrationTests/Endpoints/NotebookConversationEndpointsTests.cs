using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models;
using System.Net.Http.Headers;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class NotebookConversationEndpointsTests : BaseIntegrationTest
{
    [ClassInitialize]
    public static async Task ClassInit(TestContext ctx)
    {
        await InitializeSharedFactoryAsync(ctx);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
    }

    private async Task<HttpResponseMessage> SendMessageWithStreamingAsync(string url, object request)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = JsonContent.Create(request);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        
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

    [TestMethod]
    public async Task CreateConversation_And_SendMessage_Flow()
    {
        // Note: This integration test now requires real ChatRunner integration
        // Configure proper AI service settings and API keys to run this test
        
        // Arrange: create project + notebook via helper endpoint
        var createProjectResp = await Client.PostAsJsonAsync("/api/projects", new { title = "proj", description = "d" });
        createProjectResp.EnsureSuccessStatusCode();
        var project = await createProjectResp.Content.ReadFromJsonAsync<ProjectDto>();

        var notebookResp = await Client.PostAsJsonAsync($"/api/projects/{project!.Id}/notebooks", new { title = "nb", guideId = await GetDefaultGuideIdAsync() });
        notebookResp.EnsureSuccessStatusCode();
        var notebook = await notebookResp.Content.ReadFromJsonAsync<NotebookDto>();

        // Create conversation
        var createConvResp = await Client.PostAsJsonAsync($"/api/projects/{project.Id}/notebooks/{notebook!.Id}/conversations", new { title = "Test" });
        createConvResp.EnsureSuccessStatusCode();
        var conversation = await createConvResp.Content.ReadFromJsonAsync<NotebookConversationListDto>();

        // Send message via SSE (do not try to parse JSON from SSE stream)
        using var sendResp = await SendMessageWithStreamingAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation!.Id}/messages", new { instructions = "Hello", assistantName = "Code Executor" });
        sendResp.EnsureSuccessStatusCode();

        // Poll the conversation endpoint until at least the user message is present
        NotebookConversationWithMessagesDto? convo = null;
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        do
        {
            var getResp = await Client.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations/{conversation.Id}");
            if (getResp.IsSuccessStatusCode)
            {
                convo = await getResp.Content.ReadFromJsonAsync<NotebookConversationWithMessagesDto>();
                if (convo != null && convo.Messages != null && convo.Messages.Count >= 1)
                {
                    break;
                }
            }
            await Task.Delay(200);
        } while (DateTime.UtcNow < timeoutAt);

        convo.Should().NotBeNull();
        var messages = (convo!.Messages ?? new List<MessageDto>()).ToList();
        messages.Should().HaveCountGreaterThanOrEqualTo(1);
        string RoleOf(MessageDto m) => m.Role.ToString().ToLowerInvariant();
        var systemMessage = messages.FirstOrDefault(m => RoleOf(m) == "system");
        var userMessage = messages.FirstOrDefault(m => RoleOf(m) == "user");
        // Per requirements: system instructions are injected in-memory only and should not be persisted
        systemMessage.Should().BeNull();
        userMessage.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ListConversations_ReturnsLastActivity_AndOrdersByRecent()
    {
        var createProjectResp = await Client.PostAsJsonAsync("/api/projects", new { title = "proj", description = "d" });
        createProjectResp.EnsureSuccessStatusCode();
        var project = await createProjectResp.Content.ReadFromJsonAsync<ProjectDto>();

        var notebookResp = await Client.PostAsJsonAsync($"/api/projects/{project!.Id}/notebooks", new { title = "nb", guideId = await GetDefaultGuideIdAsync() });
        notebookResp.EnsureSuccessStatusCode();
        var notebook = await notebookResp.Content.ReadFromJsonAsync<NotebookDto>();

        var conv1Resp = await Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/notebooks/{notebook!.Id}/conversations",
            new { title = "First" });
        conv1Resp.EnsureSuccessStatusCode();
        var conv1 = await conv1Resp.Content.ReadFromJsonAsync<NotebookConversationListDto>();

        await Task.Delay(25);

        var conv2Resp = await Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations",
            new { title = "Second" });
        conv2Resp.EnsureSuccessStatusCode();
        var conv2 = await conv2Resp.Content.ReadFromJsonAsync<NotebookConversationListDto>();

        var listResp = await Client.GetAsync($"/api/projects/{project.Id}/notebooks/{notebook.Id}/conversations");
        listResp.EnsureSuccessStatusCode();
        var list = await listResp.Content.ReadFromJsonAsync<List<NotebookConversationListDto>>();

        list.Should().NotBeNull();
        list!.Count.Should().BeGreaterThanOrEqualTo(2);
        list[0].Id.Should().Be(conv2!.Id);
        list[1].Id.Should().Be(conv1!.Id);

        list[0].LastActivity.Should().BeOnOrAfter(list[1].LastActivity);
        list[0].LastActivity.Should().BeOnOrAfter(list[0].Created);
        list[1].LastActivity.Should().BeOnOrAfter(list[1].Created);
    }
} 