namespace GuideAntsApi.Options;

/// <summary>
/// Bounds for the in-memory cache of chat-ready attachment renderings
/// (see <see cref="Services.Conversations.Attachments.AttachmentRenderCache"/>).
/// </summary>
public class AttachmentRenderCacheOptions
{
    public const string SectionName = "AttachmentRenderCache";

    /// <summary>Total cache budget; entry sizes are summed rendered text/base64 lengths.</summary>
    public long SizeLimitBytes { get; set; } = 128L * 1024 * 1024;

    public int SlidingExpirationMinutes { get; set; } = 30;
}
