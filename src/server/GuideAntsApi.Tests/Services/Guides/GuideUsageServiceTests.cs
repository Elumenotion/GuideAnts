using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideUsageServiceTests
{
    [TestMethod]
    public async Task GetGuideUsageSummaryAsync_Returns_null_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-usage-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var summary = await service.GetGuideUsageSummaryAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow.AddDays(-7),
            DateTime.UtcNow);

        summary.Should().BeNull();
    }

    [TestMethod]
    public async Task GetGuideUsageSummaryAsync_Returns_summary_for_existing_guide()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-usage-summary-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                AssistantId = guideId,
                Category = UsageCategory.ChatCompletion,
                Created = DateTime.UtcNow.AddHours(-2),
                ChargeUsd = 0.5m
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var summary = await service.GetGuideUsageSummaryAsync(projectId, guideId, from, to);

        summary.Should().NotBeNull();
        summary!.GuideId.Should().Be(guideId);
    }

    [TestMethod]
    public async Task GetGuideUsageDailyBucketsAsync_Returns_null_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-buckets-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var buckets = await service.GetGuideUsageDailyBucketsAsync(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        buckets.Should().BeNull();
    }

    [TestMethod]
    public async Task GetGuideUsageDailyBucketsAsync_Returns_daily_totals()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-buckets-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var day = DateTime.UtcNow.Date;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                AssistantId = guideId,
                Category = UsageCategory.ChatCompletion,
                Created = day.AddHours(1),
                ValueInput = 100,
                ValueOutput = 50,
                ChargeUsd = 0.25m
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var buckets = await service.GetGuideUsageDailyBucketsAsync(
            projectId, guideId, day, day.AddDays(1));

        buckets.Should().NotBeNull();
        buckets!.Should().ContainSingle(b => b.Date == day);
        buckets[0].PromptTokens.Should().Be(100);
    }

    [TestMethod]
    public async Task GetInvocationAsync_Returns_agent_invocation_node()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-invocation-{Guid.NewGuid():N}");
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = assistantId,
                Name = "Researcher",
                Kind = AssistantKind.Assistant,
                Created = DateTime.UtcNow
            });
            seed.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = "notebook",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Chat",
                Created = DateTime.UtcNow
            });
            seed.AgentInvocations.Add(new AgentInvocation
            {
                Id = invocationId,
                ParentConversationId = conversationId,
                ParentTurnIndex = 0,
                AssistantId = assistantId,
                AssistantName = "Researcher",
                Instructions = "Research topic",
                Status = "completed",
                Created = DateTime.UtcNow
            });
            seed.AgentInvocationMessages.Add(new AgentInvocationMessage
            {
                AgentInvocationId = invocationId,
                Role = ChatRole.Assistant,
                Content = "Done",
                Sequence = 0,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var node = await service.GetInvocationAsync(invocationId);

        node.Should().NotBeNull();
        node!.AssistantName.Should().Be("Researcher");
        node.Messages.Should().ContainSingle(m => m.Content == "Done");
    }

    [TestMethod]
    public async Task GetGuideUsageConversationsAsync_Returns_paged_summaries()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-convos-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = "notebook",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Usage chat",
                Created = DateTime.UtcNow.AddHours(-1)
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                AssistantId = guideId,
                ConversationId = conversationId,
                AgentInvocationId = null,
                Category = UsageCategory.ChatCompletion,
                Created = DateTime.UtcNow.AddHours(-1),
                ValueInput = 10,
                ValueOutput = 5,
                ChargeUsd = 0.1m
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var page = await service.GetGuideUsageConversationsAsync(projectId, guideId, from, to, page: 1, pageSize: 10);

        page.Should().NotBeNull();
        page!.Items.Should().ContainSingle(i => i.ConversationId == conversationId);
    }

    [TestMethod]
    public async Task GetGuideUsageCrewAsync_Returns_null_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-crew-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var crew = await service.GetGuideUsageCrewAsync(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        crew.Should().BeNull();
    }

    [TestMethod]
    public async Task GetGuideUsageCrewAsync_Returns_empty_sections_for_guide_without_crew_usage()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-crew-empty-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var crew = await service.GetGuideUsageCrewAsync(projectId, guideId, from, to);

        crew.Should().NotBeNull();
        crew!.CrewMembers.Should().BeEmpty();
        crew.DirectToolCalls.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetGuideUsageConversationsAsync_Clamps_paging_and_filters_excluded_assistant_steps()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-convos-clamp-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddHours(-2);
        var to = DateTime.UtcNow.AddHours(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Notebook",
                Slug = "notebook",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Conversation",
                Created = DateTime.UtcNow.AddMinutes(-30)
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                AssistantId = guideId,
                ConversationId = conversationId,
                Category = UsageCategory.ChatCompletion,
                Created = DateTime.UtcNow.AddMinutes(-20),
                ValueInput = 15,
                ValueOutput = 9,
                ChargeUsd = 0.15m
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                Role = ChatRole.Assistant,
                AssistantId = guideId,
                AssistantName = "Conversation Title Generator",
                Content = "Title helper",
                MessageSequence = 1,
                TurnIndex = 0,
                Created = DateTime.UtcNow.AddMinutes(-19)
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                Role = ChatRole.Assistant,
                AssistantId = guideId,
                AssistantName = "Guide",
                Content = "User response",
                MessageSequence = 2,
                TurnIndex = 0,
                Created = DateTime.UtcNow.AddMinutes(-18)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var page = await service.GetGuideUsageConversationsAsync(
            projectId,
            guideId,
            from,
            to,
            page: 0,
            pageSize: 900);

        page.Should().NotBeNull();
        page!.Page.Should().Be(1);
        page.PageSize.Should().Be(500);
        page.Items.Should().ContainSingle(i => i.ConversationId == conversationId && i.AssistantSteps == 1);
    }

    [TestMethod]
    public async Task GetTurnInvocationsAsync_Uses_root_level_tool_tree_when_no_agent_invocations()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-turn-root-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnCreated = DateTime.UtcNow.AddMinutes(-8);
        var messageId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                GuideId = guideId,
                Title = "Notebook",
                Slug = "notebook",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Conversation",
                Created = turnCreated
            });
            seed.ConversationTurns.Add(new ConversationTurn
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                TurnIndex = 0,
                Created = turnCreated,
                LastUpdated = turnCreated.AddSeconds(20),
                AssistantName = "Guide",
                Instructions = "turn instructions"
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = messageId,
                NotebookConversationId = conversationId,
                TurnIndex = 0,
                Role = ChatRole.Assistant,
                Content = "message",
                MessageSequence = 1,
                Created = turnCreated.AddSeconds(1)
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                ConversationId = conversationId,
                NotebookConversationMessageId = messageId,
                Category = UsageCategory.ChatCompletion,
                Created = turnCreated.AddSeconds(2),
                ValueInput = 11,
                ValueOutput = 5,
                ChargeUsd = 0.01m
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                ConversationId = conversationId,
                NotebookConversationMessageId = messageId,
                Category = UsageCategory.ToolCall,
                Operation = "ReadWeb",
                Created = turnCreated.AddSeconds(3),
                ChargeUsd = 0.02m
            });
            seed.UsageEvents.Add(new UsageEvent
            {
                ProjectId = projectId,
                ConversationId = conversationId,
                NotebookConversationMessageId = messageId,
                Category = UsageCategory.ToolCall,
                Operation = "search_docs",
                Created = turnCreated.AddSeconds(4),
                ChargeUsd = 0.03m
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var tree = await service.GetTurnInvocationsAsync(conversationId, turnIndex: 0);

        tree.Should().NotBeNull();
        tree!.TotalTokens.Should().Be(16);
        tree.RootInvocations.Should().ContainSingle(node => node.AssistantName == "Tool: search_docs");
        tree.RootInvocations.Should().NotContain(node => node.AssistantName.Contains("ReadWeb", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GetGuideUsageSummaryAsync_Includes_crew_usage_from_invoking_and_conversation_fallback_paths()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-summary-crew-branches-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var directConversationId = Guid.NewGuid();
        var unrelatedConversationId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddHours(-4);
        var to = DateTime.UtcNow.AddHours(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });

            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = directConversationId,
                    Category = UsageCategory.ChatCompletion,
                    ValueInput = 40,
                    ValueCachedInput = 3,
                    ValueReasoning = 2,
                    ValueOutput = 10,
                    ChargeUsd = 0.11m,
                    Created = DateTime.UtcNow.AddHours(-2)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = Guid.NewGuid(),
                    InvokingAssistantId = guideId,
                    ConversationId = directConversationId,
                    AgentInvocationId = Guid.NewGuid(),
                    Category = UsageCategory.ChatCompletion,
                    ValueInput = 7,
                    ValueOutput = 4,
                    ChargeUsd = 0.09m,
                    Created = DateTime.UtcNow.AddHours(-1)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = Guid.NewGuid(),
                    InvokingAssistantId = null,
                    ConversationId = directConversationId,
                    AgentInvocationId = Guid.NewGuid(),
                    Category = UsageCategory.ChatCompletion,
                    ValueInput = 5,
                    ValueOutput = 3,
                    ChargeUsd = 0.04m,
                    Created = DateTime.UtcNow.AddMinutes(-30)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = Guid.NewGuid(),
                    InvokingAssistantId = null,
                    ConversationId = unrelatedConversationId,
                    AgentInvocationId = Guid.NewGuid(),
                    Category = UsageCategory.ChatCompletion,
                    ValueInput = 99,
                    ValueOutput = 99,
                    ChargeUsd = 1.99m,
                    Created = DateTime.UtcNow.AddMinutes(-20)
                });

            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var summary = await service.GetGuideUsageSummaryAsync(projectId, guideId, from, to);

        summary.Should().NotBeNull();
        summary!.TotalConversations.Should().Be(1);
        summary.TotalPromptTokens.Should().Be(40);
        summary.TotalCachedTokens.Should().Be(3);
        summary.TotalReasoningTokens.Should().Be(2);
        summary.TotalCompletionTokens.Should().Be(10);
        summary.TotalPromptTokensWithCrew.Should().Be(52);
        summary.TotalCompletionTokensWithCrew.Should().Be(17);
        summary.DirectCost.Should().Be(0.11m);
        summary.TotalCost.Should().Be(0.24m);
    }

    [TestMethod]
    public async Task GetGuideUsageDailyBucketsAsync_Includes_crew_only_dates_when_no_direct_usage_exists()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-daily-crew-only-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var day1 = DateTime.UtcNow.Date.AddDays(-1);
        var day2 = DateTime.UtcNow.Date;

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });

            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = conversationId,
                    Category = UsageCategory.ChatCompletion,
                    ValueInput = 10,
                    ValueOutput = 4,
                    ChargeUsd = 0.10m,
                    Created = day1.AddHours(2)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = Guid.NewGuid(),
                    InvokingAssistantId = guideId,
                    ConversationId = conversationId,
                    AgentInvocationId = Guid.NewGuid(),
                    Category = UsageCategory.ChatCompletion,
                    ValueInput = 20,
                    ValueOutput = 8,
                    ChargeUsd = 0.20m,
                    Created = day2.AddHours(3)
                });

            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var buckets = await service.GetGuideUsageDailyBucketsAsync(
            projectId,
            guideId,
            day1,
            day2.AddDays(1));

        buckets.Should().NotBeNull();
        buckets!.Should().HaveCount(2);
        buckets.Should().Contain(bucket => bucket.Date == day1 && bucket.PromptTokens == 10 && bucket.PromptTokensWithCrew == 10);
        buckets.Should().Contain(bucket => bucket.Date == day2 && bucket.PromptTokens == 0 && bucket.PromptTokensWithCrew == 20);
    }

    [TestMethod]
    public async Task GetGuideUsageCrewAsync_Computes_averages_and_filters_bridge_tool_metadata()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-crew-metrics-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var crewAssistantId = Guid.NewGuid();
        var conversation1 = Guid.NewGuid();
        var conversation2 = Guid.NewGuid();
        var from = DateTime.UtcNow.AddHours(-6);
        var to = DateTime.UtcNow.AddHours(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.AddRange(
                new Assistant
                {
                    Id = guideId,
                    Name = "Guide",
                    Kind = AssistantKind.Guide,
                    Created = DateTime.UtcNow
                },
                new Assistant
                {
                    Id = crewAssistantId,
                    Name = "Crew Helper",
                    Kind = AssistantKind.Assistant,
                    Created = DateTime.UtcNow
                });
            seed.Set<GuideMember>().Add(new GuideMember
            {
                GuideId = guideId,
                AssistantId = crewAssistantId,
                DisplayOrder = 0,
                Created = DateTime.UtcNow
            });

            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = conversation1,
                    Category = UsageCategory.ChatCompletion,
                    ChargeUsd = 0.01m,
                    Created = DateTime.UtcNow.AddHours(-5)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = conversation2,
                    Category = UsageCategory.ChatCompletion,
                    ChargeUsd = 0.01m,
                    Created = DateTime.UtcNow.AddHours(-4)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = conversation1,
                    Category = UsageCategory.ToolCall,
                    Operation = "search_docs",
                    ChargeUsd = 0.02m,
                    Created = DateTime.UtcNow.AddHours(-3)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = conversation2,
                    Category = UsageCategory.ToolCall,
                    Operation = "search_docs",
                    ChargeUsd = 0.03m,
                    Created = DateTime.UtcNow.AddHours(-2)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    ConversationId = conversation1,
                    Category = UsageCategory.ToolCall,
                    Operation = "bridge",
                    MetadataJson = "{\"assistantName\":\"Crew Helper\"}",
                    ChargeUsd = 0.50m,
                    Created = DateTime.UtcNow.AddHours(-2)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = crewAssistantId,
                    InvokingAssistantId = guideId,
                    ConversationId = conversation1,
                    AgentInvocationId = Guid.NewGuid(),
                    Category = UsageCategory.ChatCompletion,
                    ChargeUsd = 0.20m,
                    Created = DateTime.UtcNow.AddHours(-1)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = crewAssistantId,
                    InvokingAssistantId = guideId,
                    ConversationId = conversation2,
                    AgentInvocationId = Guid.NewGuid(),
                    Category = UsageCategory.ChatCompletion,
                    ChargeUsd = 0.10m,
                    Created = DateTime.UtcNow.AddMinutes(-30)
                });

            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var crew = await service.GetGuideUsageCrewAsync(projectId, guideId, from, to);

        crew.Should().NotBeNull();
        crew!.CrewMembers.Should().ContainSingle(member =>
            member.AssistantId == crewAssistantId
            && member.TotalInvocations == 2
            && member.AverageInvocationsPerConversation == 1.0
            && member.TotalCost == 0.30m);
        crew.DirectToolCalls.Should().ContainSingle(tool =>
            tool.ToolName == "search_docs"
            && tool.TotalCalls == 2
            && tool.AverageCallsPerConversation == 1.0
            && tool.TotalCost == 0.05m);
    }

    [TestMethod]
    public async Task GetGuideApiUsageReportAsync_Groups_by_source_endpoint_alias_provider_mode_and_status_family()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-api-usage-group-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow.AddDays(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    SourceChannel = "wire_api",
                    Operation = "embeddings",
                    MetadataJson = "{\"endpoint\":\"embeddings\",\"alias\":\"embeddings\",\"providerServiceMode\":\"emb-default\",\"status\":\"success\"}",
                    ChargeUsd = 0.20m,
                    Created = DateTime.UtcNow.AddHours(-5)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    SourceChannel = "wire_api",
                    Operation = "embeddings",
                    MetadataJson = "{\"endpoint\":\"embeddings\",\"alias\":\"embeddings\",\"providerServiceMode\":\"emb-default\",\"status\":\"success\"}",
                    ChargeUsd = 0.30m,
                    Created = DateTime.UtcNow.AddHours(-4)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    SourceChannel = "mcp",
                    Operation = "chat",
                    ChargeUsd = 0.10m,
                    Created = DateTime.UtcNow.AddHours(-3)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    SourceChannel = null,
                    Operation = "chat",
                    ChargeUsd = 0.05m,
                    Created = DateTime.UtcNow.AddHours(-2)
                });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var report = await service.GetGuideApiUsageReportAsync(projectId, guideId, from, to, sourceFilter: "all");

        report.Should().NotBeNull();
        report!.TotalEvents.Should().Be(4);
        report.TotalChargeUsd.Should().Be(0.65m);
        report.Rows.Should().Contain(row =>
            row.SourceChannel == "wire_api" &&
            row.Endpoint == "embeddings" &&
            row.Alias == "embeddings" &&
            row.ProviderServiceMode == "emb-default" &&
            row.StatusFamily == "success" &&
            row.Events == 2 &&
            row.ChargeUsd == 0.50m);
        report.Rows.Should().Contain(row => row.SourceChannel == "mcp" && row.Endpoint == "chat");
        report.Rows.Should().Contain(row => row.SourceChannel == "conversation" && row.Endpoint == "chat");
    }

    [TestMethod]
    public async Task GetGuideApiUsageReportAsync_Applies_source_channel_filter()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-api-usage-filter-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow.AddDays(1);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Project",
                Slug = "project",
                Created = DateTime.UtcNow
            });
            seed.Assistants.Add(new Assistant
            {
                Id = guideId,
                Name = "Guide",
                Kind = AssistantKind.Guide,
                Created = DateTime.UtcNow
            });
            seed.UsageEvents.AddRange(
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    SourceChannel = null,
                    Operation = "chat",
                    ChargeUsd = 0.10m,
                    Created = DateTime.UtcNow.AddHours(-2)
                },
                new UsageEvent
                {
                    ProjectId = projectId,
                    AssistantId = guideId,
                    SourceChannel = "wire_api",
                    Operation = "chat.completions",
                    ChargeUsd = 0.20m,
                    Created = DateTime.UtcNow.AddHours(-1)
                });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var report = await service.GetGuideApiUsageReportAsync(projectId, guideId, from, to, sourceFilter: "conversation");

        report.Should().NotBeNull();
        report!.TotalEvents.Should().Be(1);
        report.Rows.Should().ContainSingle(row => row.SourceChannel == "conversation" && row.Endpoint == "chat");
    }

}
