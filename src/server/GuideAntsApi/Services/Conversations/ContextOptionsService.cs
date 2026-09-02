using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.UserProjectContextOptions;

namespace GuideAntsApi.Services.Conversations;

public class ContextOptionsService : IContextOptionsService
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserProjectContextOptionsService _userProjectContextOptionsService;
    private readonly IStoragePathResolver _pathResolver;

    public ContextOptionsService(
        ApplicationDbContext db,
        ICurrentUserService currentUserService,
        IUserProjectContextOptionsService userProjectContextOptionsService,
        IStoragePathResolver pathResolver)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _currentUserService = currentUserService ?? throw new ArgumentNullException(nameof(currentUserService));
        _userProjectContextOptionsService = userProjectContextOptionsService ?? throw new ArgumentNullException(nameof(userProjectContextOptionsService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public async Task<Dictionary<string, string>> ResolveAsync(AssistantDefinition assistant, Guid projectId, Guid notebookId, Guid conversationId, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentUser = await ResolveCurrentUserAsync(ct);

        // Get project-scoped user-provided values for the authenticated user.
        var userValues = currentUser == null || projectId == Guid.Empty
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                await _userProjectContextOptionsService.GetOptionsAsync(currentUser.Id, projectId),
                StringComparer.OrdinalIgnoreCase);

        // Process assistant-defined context options
        if (assistant?.ContextOptions != null)
        {
            foreach (var kv in assistant.ContextOptions)
            {
                var value = kv.Value ?? string.Empty;

                // If user has provided a value for this key, use it instead
                if (userValues.ContainsKey(kv.Key))
                {
                    value = userValues[kv.Key];
                }

                // Resolve commands/placeholders
                if (!string.IsNullOrEmpty(value) && value.StartsWith("[@") && value.EndsWith("]"))
                {
                    var cmd = value.Substring(2, value.Length - 3);
                    switch (cmd)
                    {
                        case "currentDate":
                            value = DateTime.UtcNow.ToString("yyyy-MM-dd");
                            break;
                        case "userName":
                            value = currentUser?.Name ?? string.Empty;
                            break;
                        case "userEmail":
                            value = currentUser?.Email ?? string.Empty;
                            break;
                        case "files":
                            value = await ResolveFilesAsync(projectId, notebookId, conversationId, isPublished: false, ct);
                            break;
                        default:
                            // Unknown command – keep placeholder for now
                            break;
                    }
                }

                resolved[kv.Key] = value;
            }
        }

        return resolved;
    }

    private async Task<CurrentUserContext?> ResolveCurrentUserAsync(CancellationToken ct)
    {
        var currentUser = await _currentUserService.GetCurrentUserAsync(ct);
        if (currentUser == null)
        {
            return null;
        }

        return new CurrentUserContext(currentUser.UserId, currentUser.Name, currentUser.Email);
    }

    public async Task<string?> BuildContextMessageAsync(AssistantDefinition assistant, Guid projectId, Guid notebookId, Guid conversationId, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(assistant, projectId, notebookId, conversationId, ct);
        if (resolved.Count == 0) return null;

        // Replace empty values with "MISSING/UNKNOWN" for clarity
        var contextDict = resolved.ToDictionary(
            kv => kv.Key,
            kv => string.IsNullOrEmpty(kv.Value) ? "MISSING/UNKNOWN" : kv.Value
        );

        var wrapper = new { contextOptions = contextDict };
        return JsonSerializer.Serialize(wrapper);
    }

    public async Task<string?> BuildPublishedContextMessageAsync(
        AssistantDefinition assistant,
        Guid projectId,
        Guid notebookId,
        CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Process assistant-defined context options (no user overrides for published conversations)
        if (assistant?.ContextOptions != null)
        {
            foreach (var kv in assistant.ContextOptions)
            {
                var value = kv.Value ?? string.Empty;

                // Resolve commands/placeholders
                if (!string.IsNullOrEmpty(value) && value.StartsWith("[@") && value.EndsWith("]"))
                {
                    var cmd = value.Substring(2, value.Length - 3);
                    switch (cmd)
                    {
                        case "currentDate":
                            value = DateTime.UtcNow.ToString("yyyy-MM-dd");
                            break;
                        case "files":
                            value = await ResolveFilesAsync(projectId, notebookId, Guid.Empty, isPublished: true, ct);
                            break;
                        case "userName":
                        case "userEmail":
                            // Published conversations have no user - OMIT this key-value pair entirely
                            continue; // Skip to next iteration, don't add to resolved dict
                        default:
                            // Unknown command - OMIT this key-value pair entirely
                            continue; // Skip to next iteration, don't add to resolved dict
                    }
                }

                // Only add successfully resolved values
                resolved[kv.Key] = value;
            }
        }

        // If no context options resolved, return null (no context message)
        if (resolved.Count == 0) return null;

        // Replace empty values with "MISSING/UNKNOWN" for clarity
        var contextDict = resolved.ToDictionary(
            kv => kv.Key,
            kv => string.IsNullOrEmpty(kv.Value) ? "MISSING/UNKNOWN" : kv.Value
        );

        var wrapper = new { contextOptions = contextDict };
        return JsonSerializer.Serialize(wrapper);
    }

    private async Task<string> ResolveFilesAsync(
        Guid projectId,
        Guid notebookId,
        Guid conversationId,
        bool isPublished = false,
        CancellationToken ct = default)
    {
        try
        {
            var relativePaths = await ContextOptionFilesResolver.ResolvePathsAsync(
                _db,
                _pathResolver,
                projectId,
                notebookId,
                isPublished: isPublished,
                ct);

            return ContextOptionFilesFormatter.FormatConsole(relativePaths);
        }
        catch
        {
            return JsonSerializer.Serialize(new { files = Array.Empty<string>() });
        }
    }

    private sealed record CurrentUserContext(Guid Id, string Name, string Email);
}
