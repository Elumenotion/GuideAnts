using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

/// <summary>
/// Undo deletes every turn at or after its target and the next turn reuses the freed index, so a
/// compaction boundary at or past the target must be pulled back to the last surviving turn (or
/// cleared). Otherwise the turns created after the undo would be silently compacted (D1).
/// </summary>
[TestClass]
public sealed class ConversationUndoServiceCompactionBoundaryTests
{
    [TestMethod]
    public async Task Undo_of_turn_after_boundary_leaves_boundary_alone()
    {
        var (options, conversationId) = await SeedAsync(turnIndices: [1, 2, 3, 4], boundary: 3);

        await CreateService(options).UndoLastWithoutLockAsync(conversationId);

        (await ReadBoundaryAsync(options, conversationId)).Should().Be(3);
    }

    [TestMethod]
    public async Task Undo_of_the_boundary_turn_pulls_boundary_back_to_previous_turn()
    {
        var (options, conversationId) = await SeedAsync(turnIndices: [1, 2, 3], boundary: 3);

        await CreateService(options).UndoLastWithoutLockAsync(conversationId);

        (await ReadBoundaryAsync(options, conversationId)).Should().Be(2);
    }

    [TestMethod]
    public async Task Undo_that_removes_every_turn_clears_the_boundary()
    {
        var (options, conversationId) = await SeedAsync(turnIndices: [1], boundary: 1);

        await CreateService(options).UndoLastWithoutLockAsync(conversationId);

        (await ReadBoundaryAsync(options, conversationId)).Should().BeNull();
    }

    [TestMethod]
    public async Task Undo_with_gap_in_turn_indices_clamps_to_highest_surviving_turn()
    {
        // Turn 3 does not exist. Clamping to "target - 1" would give 3, a boundary with no turn
        // under it that the next created turn (index 3) would then sit at, compacting it at once.
        var (options, conversationId) = await SeedAsync(turnIndices: [1, 2, 4], boundary: 4);

        await CreateService(options).UndoLastWithoutLockAsync(conversationId);

        (await ReadBoundaryAsync(options, conversationId)).Should().Be(2);
    }

    [TestMethod]
    public async Task Undo_without_boundary_leaves_it_null()
    {
        var (options, conversationId) = await SeedAsync(turnIndices: [1, 2], boundary: null);

        await CreateService(options).UndoLastWithoutLockAsync(conversationId);

        (await ReadBoundaryAsync(options, conversationId)).Should().BeNull();
    }

    private static async Task<(DbContextOptions<ApplicationDbContext> Options, Guid ConversationId)> SeedAsync(
        int[] turnIndices, int? boundary)
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"undo-boundary-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();

        await using var seed = new ApplicationDbContext(options);
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        seed.Projects.Add(new Project { Id = projectId, Title = "P", Slug = "p", Created = DateTime.UtcNow });
        seed.Notebooks.Add(new Notebook
        {
            Id = notebookId, ProjectId = projectId, Title = "NB", Slug = "nb", Created = DateTime.UtcNow
        });
        seed.NotebookConversations.Add(new NotebookConversation
        {
            Id = conversationId, NotebookId = notebookId, Title = "Chat", Created = DateTime.UtcNow,
            CompactionBoundaryTurnIndex = boundary
        });

        foreach (var turnIndex in turnIndices)
        {
            seed.ConversationTurns.Add(new ConversationTurn
            {
                NotebookConversationId = conversationId, TurnIndex = turnIndex, AssistantName = "Guide",
                Status = "completed", Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow
            });
            seed.NotebookConversationMessages.Add(new NotebookConversationMessage
            {
                Id = Guid.NewGuid(), NotebookConversationId = conversationId, TurnIndex = turnIndex,
                MessageSequence = 1, Role = DataModelChatRole.User, Content = $"message {turnIndex}",
                Created = DateTime.UtcNow.AddMinutes(turnIndex)
            });
        }

        await seed.SaveChangesAsync();
        return (options, conversationId);
    }

    private static ConversationUndoService CreateService(DbContextOptions<ApplicationDbContext> options)
    {
        var distributedLock = Mock.Of<IDistributedConversationLock>();
        var broadcastHub = Mock.Of<IConversationBroadcastHub>();
        var scopeFactory = new TestServiceScopeFactory(new ApplicationDbContext(options));
        var policy = new PrivateConversationStreamPolicy(
            broadcastHub,
            new ConversationStreamLockCoordinator(distributedLock),
            scopeFactory,
            Mock.Of<ILogger<PrivateConversationStreamPolicy>>());

        return new ConversationUndoService(
            distributedLock,
            broadcastHub,
            policy,
            new ConversationStreamRunRegistry(),
            scopeFactory,
            Mock.Of<ILogger<ConversationUndoService>>());
    }

    private static async Task<int?> ReadBoundaryAsync(DbContextOptions<ApplicationDbContext> options, Guid conversationId)
    {
        await using var db = new ApplicationDbContext(options);
        return await db.NotebookConversations
            .Where(c => c.Id == conversationId)
            .Select(c => c.CompactionBoundaryTurnIndex)
            .SingleAsync();
    }
}
