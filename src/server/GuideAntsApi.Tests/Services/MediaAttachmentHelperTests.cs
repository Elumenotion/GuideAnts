using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class MediaAttachmentHelperTests
{
    [TestMethod]
    public void IsImageFile_Recognizes_common_image_extensions()
    {
        MediaAttachmentHelper.IsImageFile("photo.png").Should().BeTrue();
        MediaAttachmentHelper.IsImageFile("photo.JPG").Should().BeTrue();
        MediaAttachmentHelper.IsImageFile("notes.md").Should().BeFalse();
    }

    [TestMethod]
    public async Task TryGetFirstImageAttachment_Skips_path_only_rows()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"media-path-only-{Guid.NewGuid():N}");
        Guid conversationId;
        await using (var seed = new ApplicationDbContext(options))
        {
            (_, var notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(seed);
            var conversation = new NotebookConversation { NotebookId = notebookId, Title = "Path-only media" };
            seed.NotebookConversations.Add(conversation);
            await seed.SaveChangesAsync();
            conversationId = conversation.Id;

            var turn = new ConversationTurn
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                AssistantName = "assistant",
                Instructions = "attach",
                Status = "completed"
            };
            var message = new NotebookConversationMessage
            {
                NotebookConversationId = conversationId,
                TurnIndex = 1,
                MessageSequence = 1,
                Role = ChatRole.User,
                Content = "attach"
            };
            seed.ConversationTurns.Add(turn);
            seed.NotebookConversationMessages.Add(message);
            seed.MessageAttachments.Add(new MessageAttachment
            {
                MessageId = message.Id,
                RelativePath = "Host/mount.png",
                UploadType = ContentUploadType.ImageFile,
                Type = AttachmentType.Referenced,
                OrderIndex = 0
            });
            await seed.SaveChangesAsync();
        }

        var fileService = new Mock<INotebookFileService>();
        var services = new ServiceCollection()
            .AddScoped(_ => new ApplicationDbContext(options))
            .AddSingleton<INotebookFileService>(fileService.Object)
            .BuildServiceProvider();
        using (services)
        {
            var result = await MediaAttachmentHelper
                .TryGetFirstImageAttachmentForCurrentUserMessageAsync(services, conversationId);

            result.Should().BeNull();
            fileService.Verify(
                s => s.GetFileContentStreamAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }
}
