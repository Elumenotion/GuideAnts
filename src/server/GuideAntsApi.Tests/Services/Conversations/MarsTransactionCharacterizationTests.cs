using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class MarsTransactionCharacterizationTests
{
    [TestMethod]
    public void ConversationPersistence_RollsBackFailedWritesByClearingChangeTracker()
    {
        var persistenceSource = ReadGuideAntsApiSource(
            "Services",
            "Conversations",
            "Persistence",
            "ConversationPersistence.cs");

        persistenceSource.Should().Contain("ChangeTracker.Clear()");
        persistenceSource.Should().Contain("ExecuteWriteAsync");
    }

    [TestMethod]
    public void CheckpointTurnAsync_UsesAtomicConditionalUpdate()
    {
        var persistenceSource = ReadGuideAntsApiSource(
            "Services",
            "Conversations",
            "Persistence",
            "ConversationPersistence.cs");

        persistenceSource.Should().Contain("ExecuteUpdateAsync");
        persistenceSource.Should().Contain("CheckpointVersion");
        persistenceSource.Should().Contain("ExecutionId");
    }

    [TestMethod]
    public void StreamingPersistenceWrites_UseImplicitTransactions()
    {
        var persistenceSource = ReadGuideAntsApiSource(
            "Services",
            "Conversations",
            "Persistence",
            "ConversationPersistence.cs");

        var streamingMethods = new[]
        {
            ExtractMethod(
                persistenceSource,
                "public async Task<Guid> StartAssistantMessageAsync",
                "public async Task AppendOrFinalizeAssistantMessageAsync"),
            ExtractMethod(
                persistenceSource,
                "public async Task AppendOrFinalizeAssistantMessageAsync",
                "public async Task FinalizeStreamingAssistantMessageIfStillStreamingAsync"),
            ExtractMethod(
                persistenceSource,
                "public async Task FinalizeStreamingAssistantMessageIfStillStreamingAsync",
                "public async Task<CreateToolMessageResult> CreateToolMessageAsync"),
            ExtractMethod(
                persistenceSource,
                "public async Task<CreateToolMessageResult> CreateToolMessageAsync",
                "public async Task PersistRunOutputAsync"),
            ExtractMethod(
                persistenceSource,
                "public async Task<bool> TerminalizeTurnAsync",
                "public async Task<bool> CheckpointTurnAsync"),
            ExtractMethod(
                persistenceSource,
                "public async Task AppendTurnTraceSegmentAsync",
                "private static async Task ExecuteSerializableWriteAsync")
        };

        foreach (var method in streamingMethods)
        {
            method.Should().Contain("ExecuteAtomicFencedWriteAsync");
            method.Should().NotContain("ExecuteSerializableWriteAsync");
            method.Should().NotContain("BeginTransactionAsync");
        }

        persistenceSource.Should().Contain("db.Database.AutoSavepointsEnabled = false");
    }

    [TestMethod]
    public void ConversationTurnFenceFields_AreOptimisticConcurrencyTokens()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"mars-fence-{Guid.NewGuid():N}")
            .Options;

        using var db = new ApplicationDbContext(options);
        var entityType = db.Model.FindEntityType(typeof(ConversationTurn));

        entityType.Should().NotBeNull();
        entityType!.FindProperty(nameof(ConversationTurn.Status))!.IsConcurrencyToken.Should().BeTrue();
        entityType.FindProperty(nameof(ConversationTurn.ExecutionId))!.IsConcurrencyToken.Should().BeTrue();
    }

    private static string ReadGuideAntsApiSource(params string[] relativePath)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "GuideAntsApi",
                Path.Combine(relativePath)));

        File.Exists(path).Should().BeTrue($"expected source file at {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected source marker '{startMarker}'");

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"expected source marker '{endMarker}' after '{startMarker}'");

        return source[start..end];
    }
}
