using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Services.Conversations.Mapping;

/// <summary>
/// Projects persisted message attachments into the conversation wire shape.
/// Keeping this mapping in one place ensures path-only and folder attachments
/// round-trip consistently across query and message projections.
/// </summary>
public static class MessageAttachmentProjector
{
    public static AttachedFileDto ToAttachedFileDto(MessageAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        return ToAttachedFileDto(
            attachment.NotebookFileId,
            attachment.RelativePath,
            attachment.NotebookFile?.RelativePath,
            attachment.UploadType,
            attachment.NotebookFile?.FileSize ?? 0,
            attachment.Type);
    }

    /// <summary>
    /// Scalar overload used after EF has materialized the query projection.
    /// EF does not need to translate the DTO mapping itself.
    /// </summary>
    public static AttachedFileDto ToAttachedFileDto(
        Guid? notebookFileId,
        string? relativePath,
        string? notebookFileRelativePath,
        ContentUploadType? uploadType,
        long fileSize,
        AttachmentType type)
    {
        var sourcePath = string.IsNullOrWhiteSpace(relativePath)
            ? notebookFileRelativePath
            : relativePath;
        var normalizedRelativePath = NormalizeRelativePath(sourcePath);
        var fileName = GetFileName(normalizedRelativePath);

        return new AttachedFileDto(
            notebookFileId,
            normalizedRelativePath,
            uploadType,
            fileName,
            DetermineFileType(fileName, uploadType),
            fileSize,
            null,
            type);
    }

    private static string DetermineFileType(string fileName, ContentUploadType? uploadType) =>
        uploadType switch
        {
            ContentUploadType.ImageFile or ContentUploadType.ImageUrl => "image",
            ContentUploadType.AudioFile => "audio",
            ContentUploadType.TextFile => "text",
            ContentUploadType.Folder => "folder",
            ContentUploadType.SandboxFile => "other",
            _ => ConversationMessageMapper.DetermineFileTypeString(fileName)
        };

    private static string? NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return relativePath.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static string GetFileName(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "unknown";
        }

        var pathWithoutTrailingSlash = relativePath.TrimEnd('/');
        return Path.GetFileName(pathWithoutTrailingSlash) is { Length: > 0 } fileName
            ? fileName
            : pathWithoutTrailingSlash;
    }
}
