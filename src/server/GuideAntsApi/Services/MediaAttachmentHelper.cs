using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;

namespace GuideAntsApi.Services
{
    public static class MediaAttachmentHelper
    {
        public static async Task<(byte[] Bytes, string ContentType, string FileName)?> TryGetFirstImageAttachmentForCurrentUserMessageAsync(
            IServiceProvider serviceProvider,
            Guid conversationId)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var fileService = scope.ServiceProvider.GetRequiredService<INotebookFileService>();

            var currentTurn = await db.ConversationTurns
                .Where(t => t.NotebookConversationId == conversationId)
                .OrderByDescending(t => t.TurnIndex)
                .FirstOrDefaultAsync();

            if (currentTurn == null)
            {
                return null;
            }

            var userMessage = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == currentTurn.TurnIndex && m.Role == ChatRole.User)
                .OrderBy(m => m.MessageSequence)
                .FirstOrDefaultAsync();

            if (userMessage == null)
            {
                return null;
            }

            var attachments = await db.MessageAttachments
                .Include(ma => ma.NotebookFile)
                .Where(ma => ma.MessageId == userMessage.Id)
                .OrderBy(ma => ma.OrderIndex)
                .ToListAsync();

            foreach (var att in attachments)
            {
                var fileName = att.NotebookFile?.RelativePath ?? string.Empty;
                if (IsImageFile(fileName))
                {
                    var res = await fileService.GetFileContentStreamAsync(att.NotebookFileId);
                    if (res.HasValue)
                    {
                        using var stream = res.Value.Stream;
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        var imageBytes = ms.ToArray();
                        
                        // Resize image if needed to fit within supported dimensions (1024x1024, 1024x1792, 1792x1024)
                        imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(imageBytes, res.Value.ContentType);
                        
                        return (imageBytes, res.Value.ContentType, res.Value.FileName);
                    }
                }
            }

            return null;
        }

        public static bool IsImageFile(string fileName)
        {
            var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            return ext is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tiff" or "webp";
        }
    }
}





