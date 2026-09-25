using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.DataModel;

[TestClass]
public sealed class NotebookConversationCompactionBoundaryTests
{
    [TestMethod]
    public async Task CompactionBoundaryTurnIndex_DefaultsToNull_AndRoundTrips()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var notebookId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        using (var db = new ApplicationDbContext(options))
        {
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "t"
            });
            await db.SaveChangesAsync();
        }

        using (var db = new ApplicationDbContext(options))
        {
            var conv = await db.NotebookConversations.FirstAsync(c => c.Id == conversationId);
            conv.CompactionBoundaryTurnIndex.Should().BeNull();

            conv.CompactionBoundaryTurnIndex = 7;
            await db.SaveChangesAsync();
        }

        using (var db = new ApplicationDbContext(options))
        {
            var conv = await db.NotebookConversations.FirstAsync(c => c.Id == conversationId);
            conv.CompactionBoundaryTurnIndex.Should().Be(7);
        }
    }
}
