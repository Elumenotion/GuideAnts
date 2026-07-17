using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Conversations.Streaming;

public sealed class PublishedConversationStreamPolicy : ConversationStreamPolicyBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PublishedConversationStreamPolicy(
        IServiceScopeFactory scopeFactory,
        ConversationStreamLockCoordinator lockCoordinator,
        ILogger<PublishedConversationStreamPolicy> logger)
        : base(lockCoordinator, logger)
    {
        _scopeFactory = scopeFactory;
    }

    public override ConversationUsageMode UsageMode => ConversationUsageMode.Published;

    public override bool SupportsExternalToolResume => true;

    public override bool UsesProgressThrottling => false;

    public override async Task<StreamUserIdentity> ResolveUserIdentityAsync(Guid? internalUserId, string? externalUserIdentity, CancellationToken ct)
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

    public override string SanitizeAssistantContent(
        string content,
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.SanitizePublishedAssistantContent(content, filenameUrlMap, ctx);

    public override string SanitizeToolContent(string content, ConversationFileUrlContext ctx) =>
        AssistantContentSanitizer.ConvertSandboxUrlsToPublished(content, ctx);

    public override void UpdateFilenameUrlMapFromToolMessage(
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

    private static void filenameToPublishedUrl(
        IDictionary<string, string> filenameUrlMap,
        ConversationFileUrlContext ctx,
        string filename,
        string relativePath)
    {
        filenameUrlMap[filename] = AssistantContentSanitizer.BuildPublishedFileUrl(ctx, relativePath);
    }
}
