using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AntRunner.Chat;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// End-to-end coverage for per-assistant tool call limits through the real conversation
/// streaming path (fake chat provider, real SQL persistence).
/// </summary>
[TestClass]
public sealed class ToolLimitIntegrationTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        FakeChatCompletionBehavior.Instance.Reset();
        SetupAuthentication();
    }

    [TestMethod]
    public async Task SendMessageStream_ToolLimit_AllowsMaxThenSyntheticResult_AndEscalatesToForceComplete()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "Tool limit escalation");
            await SetAssistantToolLimitAsync(db, "assistant", maxToolCallsPerTurn: 1);
        }

        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.RepeatedToolCalls;

        var events = await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Keep calling tools", assistantName = "assistant" });

        events.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var db2 = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var turn = await db2.ConversationTurns.SingleAsync(t => t.NotebookConversationId == conversationId);
        turn.Status.Should().Be("completed");

        var toolMessages = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Tool)
            .OrderBy(m => m.MessageSequence)
            .ToListAsync();

        toolMessages.Should().HaveCountGreaterThanOrEqualTo(2);
        toolMessages[0].Content.Should().NotContain("Tool call limit reached");
        toolMessages.Should().Contain(m => m.Content != null && m.Content.Contains("Tool call limit reached"));

        var finalAssistant = await db2.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId
                        && m.Role == DataModelChatRole.Assistant
                        && m.ToolCalls == null
                        && m.Content != null)
            .OrderByDescending(m => m.MessageSequence)
            .FirstAsync();
        finalAssistant.Content.Should().NotBeNullOrWhiteSpace();
        finalAssistant.IsStreaming.Should().BeFalse();

        FakeChatCompletionBehavior.Instance.ToolChoiceNoneRequestCount.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task SendMessageStream_ToolLimit_CompletedTurn_RehydratesOnGetReload_T13()
    {
        Guid projectId;
        Guid notebookId;
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await SeedProjectNotebookAsync(db);
            conversationId = await SeedConversationAsync(db, notebookId, "T13 reload");
            await SetAssistantToolLimitAsync(db, "assistant", maxToolCallsPerTurn: 1);
        }

        FakeChatCompletionBehavior.Instance.Scenario = FakeChatScenario.RepeatedToolCalls;

        await SendConversationStreamToCompletionAsync(
            projectId,
            notebookId,
            conversationId,
            new { instructions = "Tool limit reload check", assistantName = "assistant" });

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var service = verifyScope.ServiceProvider.GetRequiredService<IConversationService>();
        var dto = await service.GetConversationByIdAsync(conversationId);

        dto.Should().NotBeNull();
        var finalAssistant = dto!.Messages
            .Where(m => m.Role == ChatRole.Assistant && m.ToolCalls == null)
            .OrderByDescending(m => m.Created)
            .FirstOrDefault();
        finalAssistant.Should().NotBeNull();
        finalAssistant!.Content.Should().NotBeNullOrWhiteSpace(
            "T13: limit-completed turn must rehydrate a persisted final assistant message");
    }

    private static async Task SetAssistantToolLimitAsync(
        ApplicationDbContext db,
        string assistantName,
        int maxToolCallsPerTurn)
    {
        // Match DatabaseStorage.GetAssistant ordering; update all name matches to avoid stale duplicates.
        var assistants = await db.Assistants
            .Where(a => a.Name == assistantName && a.IsActive)
            .ToListAsync();
        assistants.Should().NotBeEmpty();
        foreach (var assistant in assistants)
        {
            assistant.MaxToolCallsPerTurn = maxToolCallsPerTurn;
            assistant.Updated = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        AssistantUtility.ClearCache(assistantName);
    }

    private static async Task<(Guid projectId, Guid notebookId)> SeedProjectNotebookAsync(ApplicationDbContext db)
    {
        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive)
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"Tool Limit Project {Guid.NewGuid():N}",
            Slug = $"tl-{Guid.NewGuid():N}",
            Description = "integration",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"Tool Limit Notebook {Guid.NewGuid():N}",
            Slug = $"tl-nb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guideId,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);
        await db.SaveChangesAsync();
        return (project.Id, notebook.Id);
    }

    private static async Task<Guid> SeedConversationAsync(ApplicationDbContext db, Guid notebookId, string title)
    {
        var conv = new NotebookConversation { NotebookId = notebookId, Title = title };
        db.NotebookConversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    private async Task<List<(string EventType, string Payload)>> SendConversationStreamToCompletionAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        object requestBody)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/messages")
        {
            Content = JsonContent.Create(requestBody)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var events = new List<(string EventType, string Payload)>();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent != null)
            {
                var payload = line["data:".Length..].Trim();
                events.Add((currentEvent, payload));
            }
        }

        return events;
    }
}
