using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class GuideUsageEndpointsTests : BaseEndpointTest
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

    [TestMethod]
    public async Task Guide_usage_endpoints_return_aggregates_for_seeded_activity()
    {
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);
        var guideId = notebook.GuideId;
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddHours(1);

        await SeedUsageAsync(project.Id, notebook.Id, conversation.Id, guideId);

        var fromArg = Uri.EscapeDataString(from.ToString("O"));
        var toArg = Uri.EscapeDataString(to.ToString("O"));
        var basePath = $"/api/projects/{project.Id}/guides/{guideId}/usage";

        var summaryResponse = await Client.GetAsync($"{basePath}/summary?from={fromArg}&to={toArg}");
        var chartsResponse = await Client.GetAsync($"{basePath}/charts?from={fromArg}&to={toArg}");
        var crewResponse = await Client.GetAsync($"{basePath}/crew?from={fromArg}&to={toArg}");
        var conversationsResponse = await Client.GetAsync($"{basePath}/conversations?from={fromArg}&to={toArg}&page=1&pageSize=50");

        summaryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        chartsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        crewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        conversationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await summaryResponse.Content.ReadFromJsonAsync<GuideUsageSummaryDto>();
        var charts = await chartsResponse.Content.ReadFromJsonAsync<List<DailyUsageBucketDto>>();
        var crew = await crewResponse.Content.ReadFromJsonAsync<GuideUsageCrewDto>();
        var conversations = await conversationsResponse.Content.ReadFromJsonAsync<GuideUsageConversationsPageDto>();

        summary.Should().NotBeNull();
        summary!.GuideId.Should().Be(guideId);
        summary.TotalConversations.Should().BeGreaterThan(0);
        summary.TotalPromptTokens.Should().BeGreaterThan(0);

        charts.Should().NotBeNull();
        charts!.Should().NotBeEmpty();

        crew.Should().NotBeNull();
        crew!.DirectToolCalls.Should().Contain(call => call.ToolName == "search_docs");

        conversations.Should().NotBeNull();
        conversations!.Items.Should().Contain(item => item.ConversationId == conversation.Id);
    }

    [TestMethod]
    public async Task Conversation_invocations_endpoint_returns_tree_for_seeded_turns()
    {
        var project = await CreateTestProjectAsync();
        var notebook = await CreateTestNotebookAsync(project.Id);
        var conversation = await CreateTestConversationAsync(project.Id, notebook.Id);

        await SeedInvocationTreeAsync(project.Id, notebook.Id, conversation.Id, notebook.GuideId);

        var response = await Client.GetAsync($"/api/conversations/{conversation.Id}/invocations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<List<TurnInvocationTreeDto>>();
        payload.Should().NotBeNull();
        payload!.Should().ContainSingle();

        var rootNodes = payload[0].RootInvocations;
        rootNodes.Should().Contain(node => node.AssistantName == "Researcher");
        rootNodes.Should().Contain(node => node.AssistantName == "Tool: summarize");
        rootNodes.Should().NotContain(node => node.AssistantName.Contains("ReadWeb", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SeedUsageAsync(Guid projectId, Guid notebookId, Guid conversationId, Guid guideId)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var crewAssistantId = await db.Assistants
            .Where(a => a.Name == "Search")
            .Select(a => a.Id)
            .FirstAsync();
        var messageId = Guid.NewGuid();

        db.ConversationTurns.Add(new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            TurnIndex = 0,
            AssistantName = "Guide",
            Instructions = "Initial request",
            Created = DateTime.UtcNow.AddMinutes(-20),
            LastUpdated = DateTime.UtcNow.AddMinutes(-19)
        });

        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = messageId,
            NotebookConversationId = conversationId,
            TurnIndex = 0,
            Role = ChatRole.Assistant,
            AssistantId = guideId,
            AssistantName = "Guide",
            Content = "Answer",
            MessageSequence = 1,
            Created = DateTime.UtcNow.AddMinutes(-19)
        });

        var hasCrewMapping = await db.Set<GuideMember>()
            .AnyAsync(m => m.GuideId == guideId && m.AssistantId == crewAssistantId);
        if (!hasCrewMapping)
        {
            db.Set<GuideMember>().Add(new GuideMember
            {
                GuideId = guideId,
                AssistantId = crewAssistantId,
                DisplayOrder = 0,
                Created = DateTime.UtcNow.AddMinutes(-25)
            });
        }

        db.UsageEvents.AddRange(
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                NotebookConversationMessageId = messageId,
                AssistantId = guideId,
                Category = UsageCategory.ChatCompletion,
                ValueInput = 25,
                ValueOutput = 10,
                ChargeUsd = 0.12m,
                Created = DateTime.UtcNow.AddMinutes(-18)
            },
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                NotebookConversationMessageId = messageId,
                AssistantId = guideId,
                Category = UsageCategory.ToolCall,
                Operation = "search_docs",
                ChargeUsd = 0.03m,
                Created = DateTime.UtcNow.AddMinutes(-17)
            },
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                AssistantId = crewAssistantId,
                InvokingAssistantId = guideId,
                AgentInvocationId = Guid.NewGuid(),
                Category = UsageCategory.ChatCompletion,
                ValueInput = 8,
                ValueOutput = 4,
                ChargeUsd = 0.08m,
                Created = DateTime.UtcNow.AddMinutes(-16)
            });

        await db.SaveChangesAsync();
    }

    private static async Task SeedInvocationTreeAsync(Guid projectId, Guid notebookId, Guid conversationId, Guid guideId)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var bridgeMessageId = Guid.NewGuid();

        db.ConversationTurns.Add(new ConversationTurn
        {
            Id = Guid.NewGuid(),
            NotebookConversationId = conversationId,
            TurnIndex = 0,
            AssistantName = "Guide",
            Instructions = "Turn instructions",
            Created = DateTime.UtcNow.AddMinutes(-12),
            LastUpdated = DateTime.UtcNow.AddMinutes(-11)
        });

        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            Id = bridgeMessageId,
            NotebookConversationId = conversationId,
            TurnIndex = 0,
            Role = ChatRole.Assistant,
            AssistantId = guideId,
            AssistantName = "Guide",
            Content = "Tool output",
            ToolCallId = "tool-call-1",
            MessageSequence = 1,
            Created = DateTime.UtcNow.AddMinutes(-11)
        });

        db.AgentInvocations.AddRange(
            new AgentInvocation
            {
                Id = parentId,
                ParentConversationId = conversationId,
                ParentTurnIndex = 0,
                AssistantId = guideId,
                AssistantName = "Researcher",
                Instructions = "Parent invocation",
                Status = "completed",
                Depth = 0,
                Created = DateTime.UtcNow.AddMinutes(-11),
                Completed = DateTime.UtcNow.AddMinutes(-10),
                DurationMs = 60000,
                ToolCallCount = 1,
                LlmRoundTrips = 1
            },
            new AgentInvocation
            {
                Id = childId,
                ParentConversationId = conversationId,
                ParentTurnIndex = 0,
                ParentInvocationId = parentId,
                AssistantId = guideId,
                AssistantName = "Child Analyst",
                Instructions = "Child invocation",
                Status = "completed",
                Depth = 1,
                Created = DateTime.UtcNow.AddMinutes(-10).AddSeconds(10),
                Completed = DateTime.UtcNow.AddMinutes(-10).AddSeconds(40),
                DurationMs = 30000,
                ToolCallCount = 0,
                LlmRoundTrips = 1
            });

        db.UsageEvents.AddRange(
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                AgentInvocationId = parentId,
                Category = UsageCategory.ChatCompletion,
                ValueInput = 20,
                ValueOutput = 7,
                ChargeUsd = 0.10m,
                Created = DateTime.UtcNow.AddMinutes(-10).AddSeconds(5)
            },
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                AgentInvocationId = parentId,
                Category = UsageCategory.ToolCall,
                Operation = "search_docs",
                ChargeUsd = 0.02m,
                Created = DateTime.UtcNow.AddMinutes(-10).AddSeconds(15)
            },
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                AgentInvocationId = childId,
                Category = UsageCategory.ChatCompletion,
                ValueInput = 5,
                ValueOutput = 3,
                ChargeUsd = 0.03m,
                Created = DateTime.UtcNow.AddMinutes(-10).AddSeconds(20)
            },
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                NotebookConversationMessageId = bridgeMessageId,
                Category = UsageCategory.ToolCall,
                Operation = "ReadWeb",
                ChargeUsd = 0.01m,
                Created = DateTime.UtcNow.AddMinutes(-10).AddSeconds(25)
            },
            new UsageEvent
            {
                ProjectId = projectId,
                NotebookId = notebookId,
                ConversationId = conversationId,
                NotebookConversationMessageId = bridgeMessageId,
                Category = UsageCategory.ToolCall,
                Operation = "summarize",
                ChargeUsd = 0.02m,
                Created = DateTime.UtcNow.AddMinutes(-10).AddSeconds(26)
            });

        await db.SaveChangesAsync();
    }

    private async Task<ProjectDto> CreateTestProjectAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/projects", new { title = "Guide Usage Project", description = "Coverage test" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProjectDto>() ?? throw new InvalidOperationException("Failed to create project.");
    }

    private async Task<NotebookDto> CreateTestNotebookAsync(Guid projectId)
    {
        var guideId = await GetDefaultGuideIdAsync();
        var response = await Client.PostAsJsonAsync($"/api/projects/{projectId}/notebooks", new { title = "Guide Usage Notebook", guideId });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NotebookDto>() ?? throw new InvalidOperationException("Failed to create notebook.");
    }

    private async Task<NotebookConversationListDto> CreateTestConversationAsync(Guid projectId, Guid notebookId)
    {
        var response = await Client.PostAsJsonAsync($"/api/projects/{projectId}/notebooks/{notebookId}/conversations", new { title = "Guide Usage Conversation" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NotebookConversationListDto>() ?? throw new InvalidOperationException("Failed to create conversation.");
    }
}

