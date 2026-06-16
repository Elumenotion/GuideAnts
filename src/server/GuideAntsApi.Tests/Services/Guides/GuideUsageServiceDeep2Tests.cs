using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Tests.BackgroundJobs;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuideUsageServiceDeep2Tests
{
    // ----- GetGuideUsageReportAsync -----

    [TestMethod]
    public async Task GetGuideUsageReportAsync_ReturnsNull_WhenGuideMissing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"report-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var report = await service.GetGuideUsageReportAsync(
            Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        report.Should().BeNull();
    }

    // NOTE: GetGuideUsageReportAsync's happy path and GetAllTurnInvocationsAsync's
    // populated path are intentionally NOT covered here. Both issue a relational
    // `GroupBy(...).ToDictionaryAsync(...)` (e.g. ConversationTurns grouped by
    // conversation id, AgentInvocationMessages grouped by invocation id) that is
    // not composed into an aggregate. The EF Core InMemory provider cannot
    // translate that shape ("A 'GroupBy' operation which is not composed into
    // aggregate or projection of elements is not supported"). These require a
    // real relational database (integration project) — see task report.

    // ----- GetAllTurnInvocationsAsync (only the no-turns short circuit is InMemory-safe) -----

    [TestMethod]
    public async Task GetAllTurnInvocationsAsync_ReturnsEmpty_WhenNoTurns()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"all-turns-empty-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var trees = await service.GetAllTurnInvocationsAsync(Guid.NewGuid());

        trees.Should().BeEmpty();
    }

    // ----- GetTurnMessagesAsync -----

    [TestMethod]
    public async Task GetTurnMessagesAsync_ReturnsNull_WhenConversationMissing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"turn-msg-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        (await service.GetTurnMessagesAsync(Guid.NewGuid(), 0)).Should().BeNull();
    }

    [TestMethod]
    public async Task GetTurnMessagesAsync_ReturnsNull_WhenTurnMissing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"turn-msg-no-turn-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = "project", Created = DateTime.UtcNow });
            seed.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "NB", Slug = "nb", Created = DateTime.UtcNow });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Chat",
                Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        (await service.GetTurnMessagesAsync(conversationId, 5)).Should().BeNull();
    }

    [TestMethod]
    public async Task GetTurnMessagesAsync_ReturnsMessagesForTurn()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"turn-msg-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var turnCreated = DateTime.UtcNow.AddMinutes(-5);

        await using (var seed = new ApplicationDbContext(options))
        {
            seed.Projects.Add(new Project { Id = projectId, Title = "Project", Slug = "project", Created = DateTime.UtcNow });
            seed.Notebooks.Add(new Notebook { Id = notebookId, ProjectId = projectId, Title = "NB", Slug = "nb", Created = DateTime.UtcNow });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Chat",
                Created = turnCreated
            });
            seed.ConversationTurns.Add(new ConversationTurn
            {
                Id = Guid.NewGuid(),
                NotebookConversationId = conversationId,
                TurnIndex = 0,
                Created = turnCreated,
                LastUpdated = turnCreated.AddSeconds(10),
                AssistantName = "Guide",
                Instructions = "turn"
            });
            seed.NotebookConversationMessages.AddRange(
                new NotebookConversationMessage
                {
                    Id = Guid.NewGuid(),
                    NotebookConversationId = conversationId,
                    TurnIndex = 0,
                    Role = ChatRole.User,
                    Content = "hello",
                    MessageSequence = 0,
                    Created = turnCreated.AddSeconds(1)
                },
                new NotebookConversationMessage
                {
                    Id = Guid.NewGuid(),
                    NotebookConversationId = conversationId,
                    TurnIndex = 0,
                    Role = ChatRole.Assistant,
                    Content = "hi there",
                    MessageSequence = 1,
                    Created = turnCreated.AddSeconds(2)
                });
            await seed.SaveChangesAsync();
        }

        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuideUsageService(context, options);

        var result = await service.GetTurnMessagesAsync(conversationId, 0);

        result.Should().NotBeNull();
        result!.TurnIndex.Should().Be(0);
        result.AssistantName.Should().Be("Guide");
        result.Messages.Should().HaveCount(2);
        result.Messages[0].Content.Should().Be("hello");
    }
}
