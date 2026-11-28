using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class CreateSyntheticUserMessageTests
{
    private static readonly Guid NotebookId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid TurnId = Guid.NewGuid();
    private static readonly Guid FileA = Guid.NewGuid();
    private static readonly Guid FileB = Guid.NewGuid();

    private DbContextOptions<ApplicationDbContext> _options = null!;

    /// <summary>
    /// Builds one in-memory store (fixed name, one context instance behind the scope
    /// factory) and seeds the notebook/conversation/file rows the attachment FK needs.
    /// </summary>
    private ConversationPersistence BuildPersistence()
    {
        _options = BackgroundJobTestHelpers.CreateInMemoryOptions($"synthetic-{Guid.NewGuid():N}");

        using (var seed = new ApplicationDbContext(_options))
        {
            var projectId = Guid.NewGuid();
            seed.Projects.Add(new Project { Id = projectId, Title = "P", Slug = "p", Created = DateTime.UtcNow });
            seed.Notebooks.Add(new Notebook
            {
                Id = NotebookId,
                ProjectId = projectId,
                Title = "NB",
                Slug = "nb",
                Created = DateTime.UtcNow
            });
            seed.NotebookConversations.Add(new NotebookConversation
            {
                Id = ConversationId,
                NotebookId = NotebookId,
                Title = "synthetic persistence",
                Created = DateTime.UtcNow
            });
            seed.NotebookFiles.Add(new NotebookFile
            {
                Id = FileA,
                NotebookId = NotebookId,
                RelativePath = "shots/a.png",
                FileSize = 10,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "a",
                Created = DateTime.UtcNow
            });
            seed.NotebookFiles.Add(new NotebookFile
            {
                Id = FileB,
                NotebookId = NotebookId,
                RelativePath = "shots/b.png",
                FileSize = 10,
                LastModifiedUtc = DateTime.UtcNow,
                FileHash = "b",
                Created = DateTime.UtcNow
            });
            seed.SaveChanges();
        }

        return new ConversationPersistence(
            new TestServiceScopeFactory(new ApplicationDbContext(_options)),
            Mock.Of<ILogger<ConversationPersistence>>());
    }

    [TestMethod]
    public async Task SingleImageAttachment_CreatesUserMessageWithMixedContentTypeAndAttachmentRow()
    {
        var persistence = BuildPersistence();

        var result = await persistence.CreateSyntheticUserMessageAsync(new CreateSyntheticUserMessageRequest(
            ConversationId,
            TurnId,
            1,
            3,
            "[Attachments produced by tool call(s) above...]",
            "user",
            Guid.NewGuid(),
            [new SyntheticMessageAttachmentRequest(FileA, null, 0)]));

        result.Message.Id.Should().NotBeEmpty();
        result.Message.Role.Should().Be(DataModelChatRole.User);
        result.Message.MessageContentType.Should().Be(MessageContentType.Mixed);
        result.Message.IsSynthetic.Should().BeTrue();
        result.Message.MessageSequence.Should().Be(3);
        result.Message.Content.Should().Contain("Attachments produced by tool");

        using var db = new ApplicationDbContext(_options);
        var attachments = await db.MessageAttachments
            .Where(a => a.MessageId == result.Message.Id)
            .OrderBy(a => a.OrderIndex)
            .ToListAsync();
        attachments.Should().ContainSingle();
        attachments[0].NotebookFileId.Should().Be(FileA);
        attachments[0].RelativePath.Should().BeNull();
        attachments[0].UploadType.Should().Be(ContentUploadType.ImageFile);
        attachments[0].Type.Should().Be(AttachmentType.Referenced);
    }

    [TestMethod]
    public async Task BatchOfTwoImages_CreatesOneMessageWithTwoOrderedAttachmentRows()
    {
        var persistence = BuildPersistence();

        var result = await persistence.CreateSyntheticUserMessageAsync(new CreateSyntheticUserMessageRequest(
            ConversationId,
            TurnId,
            2,
            5,
            "caption for two images",
            "user",
            null,
            [
                new SyntheticMessageAttachmentRequest(FileA, null, 0),
                new SyntheticMessageAttachmentRequest(FileB, null, 1)
            ]));

        using var db = new ApplicationDbContext(_options);
        var attachments = await db.MessageAttachments
            .Where(a => a.MessageId == result.Message.Id)
            .OrderBy(a => a.OrderIndex)
            .ToListAsync();

        attachments.Should().HaveCount(2);
        attachments[0].NotebookFileId.Should().Be(FileA);
        attachments[0].OrderIndex.Should().Be(0);
        attachments[1].NotebookFileId.Should().Be(FileB);
        attachments[1].OrderIndex.Should().Be(1);
    }

    [TestMethod]
    public async Task PathOnlyAttachment_PersistsRelativePathWithNullFileId()
    {
        var persistence = BuildPersistence();

        var result = await persistence.CreateSyntheticUserMessageAsync(new CreateSyntheticUserMessageRequest(
            ConversationId,
            TurnId,
            3,
            2,
            "caption",
            "user",
            null,
            [new SyntheticMessageAttachmentRequest(null, "shared/plot.png", 0)]));

        using var db = new ApplicationDbContext(_options);
        var attachment = await db.MessageAttachments
            .SingleAsync(a => a.MessageId == result.Message.Id);
        attachment.NotebookFileId.Should().BeNull();
        attachment.RelativePath.Should().Be("shared/plot.png");
    }

    [TestMethod]
    public async Task NoAttachments_PersistsTextContentTypeAndNoAttachmentRows()
    {
        var persistence = BuildPersistence();

        var result = await persistence.CreateSyntheticUserMessageAsync(new CreateSyntheticUserMessageRequest(
            ConversationId,
            TurnId,
            4,
            2,
            "plain bridge text",
            "user",
            null,
            []));

        result.Message.MessageContentType.Should().Be(MessageContentType.Text);

        using var db = new ApplicationDbContext(_options);
        (await db.MessageAttachments.CountAsync(a => a.MessageId == result.Message.Id)).Should().Be(0);
    }
}
