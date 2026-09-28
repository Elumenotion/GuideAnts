using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Tests.Benchmarks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Benchmarks;

[TestClass]
public sealed class CompactionBenchmarkTests
{
    private const string ConnectionEnv = "GA_COMPACTION_BENCHMARK_DB";
    private const string OutputEnv = "GA_COMPACTION_BENCHMARK_OUT";

    private static IServiceProvider InMemory(string name)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(name));
        return services.BuildServiceProvider();
    }

    private static void AddConversation(
        ApplicationDbContext db, Guid id, IEnumerable<(int Index, string Status)> turns, params string[] toolNames)
    {
        db.NotebookConversations.Add(new NotebookConversation { Id = id, NotebookId = Guid.NewGuid(), Title = "t" });
        foreach (var (index, status) in turns)
        {
            db.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = id, TurnIndex = index, AssistantName = "a", Instructions = "i", Status = status
            });
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = id, TurnIndex = index, MessageSequence = 0,
                Role = DataModelChatRole.User, Content = $"message {index}"
            });
        }

        var seq = 1;
        foreach (var tool in toolNames)
        {
            db.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                NotebookConversationId = id, TurnIndex = 1, MessageSequence = seq++,
                Role = DataModelChatRole.Tool, FunctionName = tool, ToolCallId = $"c{seq}", Content = "{}"
            });
        }
    }

    [TestMethod]
    public async Task SelectCandidatesAsync_SkipsConversationsBelowTheCompletedTurnFloor()
    {
        var sp = InMemory(nameof(SelectCandidatesAsync_SkipsConversationsBelowTheCompletedTurnFloor));
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var longOne = Guid.NewGuid();
        var failedOnly = Guid.NewGuid();
        var none = Guid.NewGuid();
        AddConversation(db, longOne, Enumerable.Range(1, 6).Select(i => (i, "completed")));
        AddConversation(db, failedOnly, Enumerable.Range(1, 6).Select(i => (i, "failed")));
        AddConversation(db, none, []);
        await db.SaveChangesAsync();

        var candidates = await CompactionBenchmarkRunner.SelectCandidatesAsync(db, minCompletedTurns: 5, CancellationToken.None);

        candidates.Select(c => c.ConversationId).Should().Equal(longOne);
    }

    [TestMethod]
    public async Task SelectCandidatesAsync_BoundaryIsTheLastCompletedTurn_IgnoringLaterFailedTurns()
    {
        var sp = InMemory(nameof(SelectCandidatesAsync_BoundaryIsTheLastCompletedTurn_IgnoringLaterFailedTurns));
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var id = Guid.NewGuid();
        AddConversation(db, id, Enumerable.Range(1, 5).Select(i => (i, "completed")).Append((6, "failed")));
        await db.SaveChangesAsync();

        var candidate = (await CompactionBenchmarkRunner.SelectCandidatesAsync(db, 5, CancellationToken.None)).Single();

        candidate.BoundaryTurnIndex.Should().Be(5);
        candidate.CompletedTurns.Should().Be(5);
    }

    [TestMethod]
    public async Task SelectCandidatesAsync_ClassifiesCodingByToolName_IncludingCrewCodeExecutor()
    {
        var sp = InMemory(nameof(SelectCandidatesAsync_ClassifiesCodingByToolName_IncludingCrewCodeExecutor));
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var crew = Guid.NewGuid();
        var search = Guid.NewGuid();
        var chat = Guid.NewGuid();
        AddConversation(db, crew, Enumerable.Range(1, 5).Select(i => (i, "completed")), "Code_Executor");
        AddConversation(db, search, Enumerable.Range(1, 5).Select(i => (i, "completed")), "SearchAssistantFiles");
        AddConversation(db, chat, Enumerable.Range(1, 5).Select(i => (i, "completed")));
        await db.SaveChangesAsync();

        var byId = (await CompactionBenchmarkRunner.SelectCandidatesAsync(db, 5, CancellationToken.None))
            .ToDictionary(c => c.ConversationId);

        byId[crew].Domain.Should().Be("coding");
        byId[search].Domain.Should().Be("non-coding");
        byId[chat].Domain.Should().Be("non-coding");
        byId[search].ToolNames.Should().Equal("SearchAssistantFiles");
    }

    [TestMethod]
    public void CountSectionLines_IgnoresPlaceholdersAndCountsUnknownOutcomes()
    {
        const string summary =
            "framing\n\n[Compacted 4 earlier message(s).]\n\n## Goal\nBuild a report\n\n## Artifacts\n(none)\n\n" +
            "## Activity ledger\n- search_notebook q — unknown\n- run_python — ok\n\n## Unresolved errors\n(none)\n\n" +
            "## Directives\n(none)";

        var counts = CompactionBenchmarkRunner.CountSectionLines(summary);

        counts["Goal"].Should().Be(1);
        counts["Artifacts"].Should().Be(0);
        counts["Activity ledger"].Should().Be(2);
        counts["Unresolved errors"].Should().Be(0);
        counts["Directives"].Should().Be(0);
        CompactionBenchmarkRunner.CountUnknownOutcomes(summary).Should().Be(1);
    }

    [TestMethod]
    public async Task RunAsync_UsesTheProductionSummaryPath_AndIsDeterministic()
    {
        var name = nameof(RunAsync_UsesTheProductionSummaryPath_AndIsDeterministic);
        var sp = InMemory(name);
        var id = Guid.NewGuid();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AddConversation(db, id, Enumerable.Range(1, 5).Select(i => (i, "completed")));
            await db.SaveChangesAsync();
        }

        var builder = new ConversationHistoryBuilder(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IContextOptionsService>(),
            Mock.Of<IAttachmentContentService>(),
            NullLogger<ConversationHistoryBuilder>.Instance);

        using var readScope = sp.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidate = (await CompactionBenchmarkRunner.SelectCandidatesAsync(readDb, 5, CancellationToken.None)).Single();

        var entry = await CompactionBenchmarkRunner.RunAsync(builder, readDb, candidate, CancellationToken.None);

        entry.SummaryText.Should().Contain("## Goal").And.Contain("message 1");
        entry.Deterministic.Should().BeTrue("spec Testing item 2: identical input yields byte-identical output");
        entry.PreBoundaryChars.Should().Be(Enumerable.Range(1, 5).Sum(i => $"message {i}".Length));
        CompactionBenchmarkRunner.RenderConversationReport(entry).Should().Contain(candidate.ShortId);
    }

    [TestMethod]
    [TestCategory("Benchmark")]
    public async Task Benchmark_RealTranscripts_WritesReports()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionEnv);
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Inconclusive($"Set {ConnectionEnv} to a SQL Server connection string (read-only use) to run the benchmark.");
        }

        var output = Environment.GetEnvironmentVariable(OutputEnv)
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".compaction-benchmark"));
        Directory.CreateDirectory(output);

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(connection));
        var sp = services.BuildServiceProvider();
        var builder = new ConversationHistoryBuilder(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IContextOptionsService>(),
            Mock.Of<IAttachmentContentService>(),
            NullLogger<ConversationHistoryBuilder>.Instance);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var candidates = await CompactionBenchmarkRunner.SelectCandidatesAsync(db, minCompletedTurns: 5, CancellationToken.None);
        candidates.Should().NotBeEmpty($"{ConnectionEnv} points at a database with no conversation of 5+ completed turns");

        var entries = new List<BenchmarkEntry>();
        foreach (var candidate in candidates)
        {
            var entry = await CompactionBenchmarkRunner.RunAsync(builder, db, candidate, CancellationToken.None);
            entries.Add(entry);
            await File.WriteAllTextAsync(
                Path.Combine(output, $"{candidate.ShortId}.md"), CompactionBenchmarkRunner.RenderConversationReport(entry));
        }

        await File.WriteAllTextAsync(Path.Combine(output, "index.md"), CompactionBenchmarkRunner.RenderIndex(entries));

        entries.Should().OnlyContain(e => e.Deterministic, "spec Testing item 2 must hold on real transcripts too");
    }
}
