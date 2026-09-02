using AntRunner.Chat.Abstractions;
using GuideAntsApi.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Conversations.Attachments;

/// <summary>
/// Per-process cache of chat-ready attachment content keyed by (NotebookFileId, LastModifiedUtc.Ticks).
/// A file change rotates the key, so stale entries need no invalidation hooks — they die by
/// size pressure or sliding expiration. Uses a dedicated MemoryCache instance so the size limit
/// doesn't force Size declarations onto the app's shared IMemoryCache users.
/// </summary>
public interface IAttachmentRenderCache
{
    bool TryGet(Guid notebookFileId, long lastModifiedTicks, out List<ChatContent> contents);
    void Set(Guid notebookFileId, long lastModifiedTicks, List<ChatContent> contents);
}

public sealed class AttachmentRenderCache : IAttachmentRenderCache, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _slidingExpiration;

    public AttachmentRenderCache(IOptions<AttachmentRenderCacheOptions> options)
    {
        var value = options.Value;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = value.SizeLimitBytes });
        _slidingExpiration = TimeSpan.FromMinutes(value.SlidingExpirationMinutes);
    }

    public bool TryGet(Guid notebookFileId, long lastModifiedTicks, out List<ChatContent> contents)
    {
        if (_cache.TryGetValue((notebookFileId, lastModifiedTicks), out List<ChatContent>? cached) && cached != null)
        {
            // ChatContent is immutable; only the list wrapper needs copying.
            contents = new List<ChatContent>(cached);
            return true;
        }

        contents = [];
        return false;
    }

    public void Set(Guid notebookFileId, long lastModifiedTicks, List<ChatContent> contents)
    {
        if (contents.Count == 0)
        {
            return;
        }

        var size = contents.Sum(c => (long)(c.Text?.Length ?? 0) + (c.ImageUrl?.Url.Length ?? 0));
        _cache.Set(
            (notebookFileId, lastModifiedTicks),
            new List<ChatContent>(contents),
            new MemoryCacheEntryOptions
            {
                Size = Math.Max(size, 1),
                SlidingExpiration = _slidingExpiration
            });
    }

    public void Dispose() => _cache.Dispose();
}
