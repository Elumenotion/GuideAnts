using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationRecallServiceTests
{
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static NotebookConversationMessage Msg(
        Guid conversationId, int turnIndex, int sequence, string content,
        DataModelChatRole role = DataModelChatRole.Assistant, string? functionName = null) => new()
        {
            NotebookConversationId = conversationId,
            TurnIndex = turnIndex,
            MessageSequence = sequence,
            Role = role,
            FunctionName = functionName,
            Content = content,
            Created = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc).AddMinutes(turnIndex)
        };

    /// <summary>Seeds one conversation with a boundary and returns a ready service.</summary>
    private static async Task<(ConversationRecallService Service, Guid ConversationId)> SeedAsync(
        string dbName, int? boundary, params NotebookConversationMessage[] messages)
    {
        var scopeFactory = BuildScopeFactory(dbName);
        var conversationId = messages.Length > 0 ? messages[0].NotebookConversationId : Guid.NewGuid();

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = Guid.NewGuid(),
                Title = "t",
                CompactionBoundaryTurnIndex = boundary
            });
            db.NotebookConversationMessages.AddRange(messages);
            await db.SaveChangesAsync();
        }

        return (new ConversationRecallService(scopeFactory, NullLogger<ConversationRecallService>.Instance),
                conversationId);
    }

    [TestMethod]
    public async Task RecallAsync_RanksBroaderTermCoverageAboveRepetitionOfACommonTerm()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_RanksBroaderTermCoverageAboveRepetitionOfACommonTerm),
            boundary: 5,
            Msg(conversationId, 1, 0, "alpha beta"),
            Msg(conversationId, 2, 0, "alpha alpha alpha alpha"),
            Msg(conversationId, 3, 0, "alpha gamma"));

        var page = await service.RecallAsync(conversationId, "alpha beta", 1);

        page.TotalMatches.Should().Be(3);
        page.Results.Select(r => r.TurnIndex).Should().ContainInOrder(new[] { 1, 2, 3 },
            "matching both terms must outrank repeating the term that appears everywhere");
    }

    [TestMethod]
    public async Task RecallAsync_SearchesTheBoundaryTurnItselfButNothingAfterIt()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_SearchesTheBoundaryTurnItselfButNothingAfterIt),
            boundary: 2,
            Msg(conversationId, 1, 0, "widget in the first turn"),
            Msg(conversationId, 2, 0, "widget on the boundary turn"),
            Msg(conversationId, 3, 0, "widget after the boundary"));

        var page = await service.RecallAsync(conversationId, "widget", 1);

        page.Results.Select(r => r.TurnIndex).Should().BeEquivalentTo(new[] { 1, 2 },
            "the history builder summarizes TurnIndex <= boundary, so recall must cover exactly that set");
    }

    [TestMethod]
    public async Task RecallAsync_NeverReturnsMessagesFromAnotherConversation()
    {
        var conversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var scopeFactory = BuildScopeFactory(nameof(RecallAsync_NeverReturnsMessagesFromAnotherConversation));

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.NotebookConversations.AddRange(
                new NotebookConversation { Id = conversationId, NotebookId = Guid.NewGuid(), Title = "mine", CompactionBoundaryTurnIndex = 5 },
                new NotebookConversation { Id = otherConversationId, NotebookId = Guid.NewGuid(), Title = "theirs", CompactionBoundaryTurnIndex = 5 });
            db.NotebookConversationMessages.AddRange(
                Msg(conversationId, 1, 0, "my own secret"),
                Msg(otherConversationId, 1, 0, "someone else's secret"));
            await db.SaveChangesAsync();
        }

        var service = new ConversationRecallService(scopeFactory, NullLogger<ConversationRecallService>.Instance);

        var page = await service.RecallAsync(conversationId, "secret", 1);

        page.TotalMatches.Should().Be(1);
        page.Results.Single().Excerpt.Should().Contain("my own secret");
        page.Results.Should().NotContain(r => r.Excerpt.Contains("someone else"));
    }

    [TestMethod]
    public async Task RecallAsync_PaginatesWithHasMoreOnAllButTheLastPage()
    {
        var conversationId = Guid.NewGuid();
        var messages = Enumerable.Range(1, 7)
            .Select(i => Msg(conversationId, i, 0, $"widget number {i}"))
            .ToArray();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_PaginatesWithHasMoreOnAllButTheLastPage), boundary: 10, messages);

        var first = await service.RecallAsync(conversationId, "widget", 1);
        var second = await service.RecallAsync(conversationId, "widget", 2);

        first.PageSize.Should().Be(5);
        first.Results.Should().HaveCount(5);
        first.TotalMatches.Should().Be(7);
        first.HasMore.Should().BeTrue();

        second.Results.Should().HaveCount(2);
        second.HasMore.Should().BeFalse();
        second.Results.Select(r => r.TurnIndex).Should().NotIntersectWith(first.Results.Select(r => r.TurnIndex));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-3)]
    public async Task RecallAsync_NonPositivePage_ClampsToPageOne(int requestedPage)
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            $"{nameof(RecallAsync_NonPositivePage_ClampsToPageOne)}-{requestedPage}",
            boundary: 5,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", requestedPage);

        page.Page.Should().Be(1);
        page.Results.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task RecallAsync_PageBeyondTheEnd_ReturnsEmptyResultsButHonestTotal()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_PageBeyondTheEnd_ReturnsEmptyResultsButHonestTotal),
            boundary: 5,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", 9);

        page.Results.Should().BeEmpty();
        page.TotalMatches.Should().Be(1);
        page.HasMore.Should().BeFalse();
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("a !")]
    public async Task RecallAsync_QueryWithNoUsableTerms_ReturnsAnEmptyPage(string query)
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            $"{nameof(RecallAsync_QueryWithNoUsableTerms_ReturnsAnEmptyPage)}-{query.Length}-{query.Trim()}",
            boundary: 5,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, query, 1);

        page.Results.Should().BeEmpty();
        page.TotalMatches.Should().Be(0,
            "an unusable query must never dump the whole pre-boundary history back into the context");
    }

    [TestMethod]
    public async Task RecallAsync_NoBoundary_ReturnsAnEmptyPage()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_NoBoundary_ReturnsAnEmptyPage),
            boundary: null,
            Msg(conversationId, 1, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", 1);

        page.BoundaryTurnIndex.Should().BeNull();
        page.Results.Should().BeEmpty();
        page.TotalMatches.Should().Be(0);
    }

    [TestMethod]
    public async Task RecallAsync_EmptyAndWhitespaceContent_NeitherThrowsNorMatches()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_EmptyAndWhitespaceContent_NeitherThrowsNorMatches),
            boundary: 5,
            Msg(conversationId, 1, 0, string.Empty),
            Msg(conversationId, 2, 0, "    "),
            Msg(conversationId, 3, 0, "widget"));

        var page = await service.RecallAsync(conversationId, "widget", 1);

        page.Results.Should().ContainSingle();
        page.Results.Single().TurnIndex.Should().Be(3);
    }

    [TestMethod]
    public async Task RecallAsync_MatchAtTheEndOfALongMessage_ReturnsABoundedExcerptAroundTheMatch()
    {
        var conversationId = Guid.NewGuid();
        var content = new string('x', 5000) + " widget tail";
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_MatchAtTheEndOfALongMessage_ReturnsABoundedExcerptAroundTheMatch),
            boundary: 5,
            Msg(conversationId, 1, 0, content));

        var excerpt = (await service.RecallAsync(conversationId, "widget", 1)).Results.Single().Excerpt;

        excerpt.Should().Contain("widget");
        excerpt.Length.Should().BeLessThanOrEqualTo(602, "600 content chars plus at most two ellipses");
        excerpt.Should().StartWith("…", "the excerpt window opens after the start of the message");
    }

    [TestMethod]
    public async Task RecallAsync_ExcerptCutLandingOnAnAstralCharacter_NeverSplitsASurrogatePair()
    {
        var conversationId = Guid.NewGuid();
        // "widget " is 7 units, so the emoji run starts at index 7 and every ODD index is a high
        // surrogate. An unguarded 600-unit window ends at index 599 -- odd -- and would split a pair.
        var content = "widget " + string.Concat(Enumerable.Repeat("\U0001F600", 2000));
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_ExcerptCutLandingOnAnAstralCharacter_NeverSplitsASurrogatePair),
            boundary: 5,
            Msg(conversationId, 1, 0, content));

        var excerpt = (await service.RecallAsync(conversationId, "widget", 1)).Results.Single().Excerpt;
        var body = excerpt.Trim('…');

        body.Should().NotBeEmpty();
        char.IsHighSurrogate(body[^1]).Should().BeFalse("a trailing high surrogate is half of a split pair");
        char.IsLowSurrogate(body[0]).Should().BeFalse("a leading low surrogate is half of a split pair");
        System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(body))
            .Should().Be(body, "invalid UTF-16 does not survive a UTF-8 round trip");
    }

    [TestMethod]
    public async Task RecallAsync_EqualScores_OrdersByTurnThenSequenceDeterministically()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_EqualScores_OrdersByTurnThenSequenceDeterministically),
            boundary: 9,
            Msg(conversationId, 4, 1, "widget"),
            Msg(conversationId, 2, 1, "widget"),
            Msg(conversationId, 2, 0, "widget"));

        var first = await service.RecallAsync(conversationId, "widget", 1);
        var second = await service.RecallAsync(conversationId, "widget", 1);

        first.Results.Select(r => (r.TurnIndex, r.MessageSequence))
            .Should().ContainInOrder((2, 0), (2, 1), (4, 1));
        second.Results.Should().BeEquivalentTo(first.Results, o => o.WithStrictOrdering());
    }

    [TestMethod]
    public async Task RecallAsync_NonAsciiQuery_FindsNonAsciiContent()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_NonAsciiQuery_FindsNonAsciiContent),
            boundary: 5,
            Msg(conversationId, 1, 0, "новый виджет для проекта"),
            Msg(conversationId, 2, 0, "not related at all"));

        var page = await service.RecallAsync(conversationId, "виджет", 1);

        page.TotalMatches.Should().Be(1);
        page.Results.Single().TurnIndex.Should().Be(1);
        page.Results.Single().Excerpt.Should().Contain("виджет");
    }

    [TestMethod]
    public async Task RecallAsync_PreservesMessageStructureOnEachHit()
    {
        var conversationId = Guid.NewGuid();
        var (service, _) = await SeedAsync(
            nameof(RecallAsync_PreservesMessageStructureOnEachHit),
            boundary: 5,
            Msg(conversationId, 2, 3, "{\"stdout\":\"widget built\"}",
                DataModelChatRole.Tool, functionName: "run_python"));

        var hit = (await service.RecallAsync(conversationId, "widget", 1)).Results.Single();

        hit.TurnIndex.Should().Be(2);
        hit.MessageSequence.Should().Be(3);
        hit.Role.Should().Be("Tool");
        hit.FunctionName.Should().Be("run_python");
    }
}
