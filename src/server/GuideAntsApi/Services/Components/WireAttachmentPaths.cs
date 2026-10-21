namespace GuideAntsApi.Services.Components;

///<summary>
/// Where the wire API materializes client-supplied images inside a notebook, and how to
/// recognize those files. They are per-request scratch space, not user content: they stay out
/// of sync indexing and out of doc intelligence (OCR), and chat assembly must not wait on a
/// markdown shadow that is never produced for them.
///</summary>
public static class WireAttachmentPaths
{
    public const string AttachmentFolder = "Output/.wire-attachments";

    public static bool IsWireAttachmentRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').Trim().TrimStart('/');
        return normalized.StartsWith(AttachmentFolder + "/", StringComparison.OrdinalIgnoreCase);
    }
}
