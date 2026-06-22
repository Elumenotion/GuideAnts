using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class PublishedConversationStreamPolicy : IConversationStreamPolicy
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PublishedConversationStreamPolicy(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public ConversationUsageMode UsageMode => ConversationUsageMode.Published;

    public bool SupportsExternalToolResume => true;

    public bool UsesProgressThrottling => false;

    public async Task<StreamUserIdentity> ResolveUserIdentityAsync(Guid? internalUserId, string? externalUserIdentity, CancellationToken ct)
    {
        if (internalUserId.HasValue)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == internalUserId.Value)
                .Select(u => new { u.Name, u.Email })
                .FirstOrDefaultAsync(ct);

            var displayName = !string.IsNullOrWhiteSpace(user?.Name)
                ? user!.Name
                : user?.Email ?? "User";

            return new StreamUserIdentity(internalUserId, displayName, externalUserIdentity);
        }

        return new StreamUserIdentity(null, "User", externalUserIdentity);
    }

    public ConversationFileUrlContext BuildFileUrlContext(
        NotebookConversation conversation,
        string? publisherId,
        string? hostUrl) =>
        new(
            conversation.Notebook.ProjectId,
            conversation.NotebookId,
            conversation.Id,
            publisherId,
            hostUrl);

    public string SanitizeAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.SanitizePublishedAssistantContent(content, filenameUrlMap, ctx);

    public string SanitizeToolContent(string content, ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.ConvertSandboxUrlsToPublished(content, ctx);

    public void UpdateFilenameUrlMapFromToolMessage(
        string sanitizedToolContent,
        ConversationFileUrlContext ctx,
        IDictionary<string, string> filenameUrlMap,
        NotebookConversation conversation)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var newMappings = AssistantContentSanitizer.ExtractPublishedFilenamePathMapFromToolMessage(sanitizedToolContent);
            foreach (var kvp in newMappings)
            {
                var relativePath = kvp.Value;
                if (!relativePath.StartsWith("Runs/", StringComparison.OrdinalIgnoreCase) &&
                    !relativePath.StartsWith("Output/", StringComparison.OrdinalIgnoreCase) &&
                    !relativePath.Contains('/'))
                {
                    var dbFile = db.NotebookFiles
                        .AsNoTracking()
                        .Where(f => f.NotebookId == conversation.NotebookId)
                        .Where(f => f.RelativePath.EndsWith("/" + kvp.Key) || f.RelativePath == kvp.Key)
                        .OrderByDescending(f => f.LastModifiedUtc)
                        .FirstOrDefault();

                    if (dbFile != null)
                    {
                        relativePath = dbFile.RelativePath;
                    }
                }

                filenameToPublishedUrl(filenameUrlMap, ctx, kvp.Key, relativePath);
            }
        }
        catch
        {
            // best-effort
        }
    }

    public Task<IStreamLockHandle> TryAcquireStreamAsync(
        Guid conversationId,
        StreamUserIdentity user,
        CancellationToken ct) =>
        Task.FromResult<IStreamLockHandle>(NoOpStreamLockHandle.Instance);

    public Task OnTurnCreatedAsync(Guid conversationId, StreamTurnCreatedInfo info, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnStreamingStartedAsync(Guid conversationId, StreamStreamingStartedInfo info, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnUnlockAsync(Guid conversationId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnCompleteAsync(Guid conversationId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task BroadcastStreamingProgressAsync(
        Guid conversationId,
        StreamUserIdentity user,
        int contentLength,
        int tokensProcessed,
        CancellationToken ct) =>
        Task.CompletedTask;

    public Task BroadcastEventAsync(Guid conversationId, StreamingEvent ev, CancellationToken ct) =>
        Task.CompletedTask;

    private static void filenameToPublishedUrl(
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx,
        string filename,
        string relativePath)
    {
        filenameUrlMap[filename] = AssistantContentSanitizer.BuildPublishedFileUrl(ctx, relativePath);
    }

    private sealed class NoOpStreamLockHandle : IStreamLockHandle
    {
        public static readonly NoOpStreamLockHandle Instance = new();

        public bool ConversationLockEventSent => false;

        public Task<bool> ReleaseAsync(CancellationToken ct) => Task.FromResult(false);
    }
}
