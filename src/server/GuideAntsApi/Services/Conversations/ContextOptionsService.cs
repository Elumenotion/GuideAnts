using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.UserProjectContextOptions;

namespace GuideAntsApi.Services.Conversations;

public class ContextOptionsService : IContextOptionsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserProjectContextOptionsService _userProjectContextOptionsService;

    public ContextOptionsService(IServiceScopeFactory scopeFactory, IUserProjectContextOptionsService userProjectContextOptionsService)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _userProjectContextOptionsService = userProjectContextOptionsService ?? throw new ArgumentNullException(nameof(userProjectContextOptionsService));
    }

    public async Task<Dictionary<string, string>> ResolveAsync(AssistantDefinition assistant, Guid projectId, Guid notebookId, Guid conversationId, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentUser = await ResolveCurrentUserAsync(ct);

        // Get project-scoped user-provided values for the OSS-lite single user.
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
                            value = await ResolveFilesAsync(projectId, notebookId, conversationId, ct);
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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Users
            .AsNoTracking()
            .OrderBy(u => u.Created)
            .ThenBy(u => u.Id)
            .Select(u => new CurrentUserContext(u.Id, u.Name, u.Email))
            .FirstOrDefaultAsync(ct);
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

    public string? BuildPublishedContextMessage(AssistantDefinition assistant, Guid projectId, Guid notebookId)
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
                            value = ResolveFilesForPublished(projectId, notebookId);
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

    private async Task<string> ResolveFilesAsync(Guid projectId, Guid notebookId, Guid conversationId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get all files for this notebook
            var absolutePaths = await db.NotebookFiles
                .Where(f => f.NotebookId == notebookId)
                .Select(f => f.RelativePath)
                .OrderBy(f => f)
                .ToListAsync(ct);

            // Private notebooks execute from Output/ so we emit filenames relative to that directory
            var relativePaths = absolutePaths
                .Select(p => ConvertToRelativePath(p, isPublished: false, runId: null))
                .ToList();

            var formatted = FormatFilesConsole(relativePaths);

            return formatted;
        }
        catch
        {
            return JsonSerializer.Serialize(new { files = Array.Empty<string>() });
        }
    }

    private string ResolveFilesForPublished(Guid projectId, Guid notebookId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get all files for this notebook
            var absolutePaths = db.NotebookFiles
                .Where(f => f.NotebookId == notebookId)
                .Select(f => f.RelativePath)
                .OrderBy(f => f)
                .ToList();

            // Published guides execute from Runs/{runId}/ (two levels down from notebook root)
            var relativePaths = absolutePaths
                .Select(p => ConvertToRelativePath(p, isPublished: true, runId: null))
                .ToList();

            var formatted = FormatFilesConsole(relativePaths);

            return formatted;
        }
        catch
        {
            return JsonSerializer.Serialize(new { files = Array.Empty<string>() });
        }
    }

    /// <summary>
    /// Converts a path (from notebook root) to a path relative to the CWD.
    /// Private notebooks execute from Output/, published from Runs/{runId}/.
    /// </summary>
    /// <param name="notebookRootPath">The path from notebook root (e.g., "Output/file.png", "Resources/crew-X/api.py")</param>
    /// <param name="isPublished">Whether this is a published notebook (uses Runs folder) or private (uses Output folder)</param>
    /// <param name="runId">The run ID for published notebooks (unused in current implementation)</param>
    private static string ConvertToRelativePath(string notebookRootPath, bool isPublished, string? runId)
    {
        if (string.IsNullOrWhiteSpace(notebookRootPath)) return notebookRootPath;

        // Normalize to forward slashes and trim
        var normalized = notebookRootPath.Replace('\\', '/').Trim().TrimStart('/');

        if (isPublished)
        {
            // Published notebooks execute from Runs/{runId}/ (two levels down from notebook root)
            // All files need ../../ prefix to navigate up to notebook root
            return $"../../{normalized}";
        }
        else
        {
            // Private notebooks execute from Output/ (one level down from notebook root)
            const string outputPrefix = "Output/";
            if (normalized.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // File is inside Output/ - strip prefix since it's in CWD
                return normalized[outputPrefix.Length..];
            }
            else
            {
                // File is at notebook root or elsewhere - prepend ../ to go up from Output/
                return $"../{normalized}";
            }
        }
    }

    /// <summary>
    /// Formats a list of relative paths to resemble console output wrapped in a markdown code fence.
    /// This improves LLM attention compared to a JSON array.
    /// Example:
    /// ```console
    /// README.md
    /// Output/image.png
    /// ```
    /// </summary>
    internal static string FormatFilesConsole(IEnumerable<string> paths)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```console");
        foreach (var p in paths)
        {
            sb.AppendLine(p);
        }
        sb.Append("```" /* no newline after closing fence */);
        return sb.ToString();
    }

    private sealed record CurrentUserContext(Guid Id, string Name, string Email);
}
