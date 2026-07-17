using FluentAssertions;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class ConversationLockRestartReconciliationTests
{
    [TestMethod]
    public async Task ClearAllLocksAsync_RemovesLocksLeftByInterruptedStreams()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"clear-locks-{Guid.NewGuid():N}");
        var conversationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();

        await using (var db = new ApplicationDbContext(options))
        {
            db.Projects.Add(new Project
            {
                Id = projectId,
                Title = "Restart Lock Project",
                Slug = "restart-lock-project",
                Created = DateTime.UtcNow
            });
            db.Notebooks.Add(new Notebook
            {
                Id = notebookId,
                ProjectId = projectId,
                Title = "Restart Lock Notebook",
                Slug = "restart-lock-notebook",
                Created = DateTime.UtcNow
            });
            db.NotebookConversations.Add(new NotebookConversation
            {
                Id = conversationId,
                NotebookId = notebookId,
                Title = "Locked conversation",
                Created = DateTime.UtcNow
            });
            db.ConversationLocks.Add(new ConversationLock
            {
                ConversationId = conversationId,
                LockedByUserName = "Doug Ware",
                LockedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var cleared = await ConversationLockRestartReconciliation.ClearAllLocksAsync(db);
            cleared.Should().Be(1);
        }

        await using var verify = new ApplicationDbContext(options);
        (await verify.ConversationLocks.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ClearAllLocksAsync_ReturnsZeroWhenNoLocksExist()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"clear-locks-empty-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        (await ConversationLockRestartReconciliation.ClearAllLocksAsync(db)).Should().Be(0);
    }
}
