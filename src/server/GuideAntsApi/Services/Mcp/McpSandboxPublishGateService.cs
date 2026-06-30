using System.Text.Json;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Mcp;

public sealed class McpSandboxPublishGateService(
    McpSandboxAdminApiClient adminClient) : IMcpSandboxPublishGateService
{
    public const string PublishBlockMessage =
        "Sandbox MCP packages are staged but not applied. Apply sandbox packages in Guide Builder before publishing.";

    public async Task<string?> GetPublishBlockMessageAsync(
        Guid projectId,
        Guid guideId,
        IReadOnlyList<CustomToolDto>? customTools,
        CancellationToken cancellationToken = default)
    {
        if (!HasSandboxSubprocessMcpSource(customTools))
        {
            return null;
        }

        var scopeQuery = McpSandboxAdminApiClient.BuildScopedQuery(projectId, guideId);
        using var statusDoc = await adminClient.GetJsonAsync("setup-status", scopeQuery, cancellationToken);
        if (statusDoc is null)
        {
            return PublishBlockMessage;
        }

        return HasPendingSandboxApply(statusDoc.RootElement) ? PublishBlockMessage : null;
    }

    internal static bool HasSandboxSubprocessMcpSource(IReadOnlyList<CustomToolDto>? customTools)
    {
        if (customTools is null || customTools.Count == 0)
        {
            return false;
        }

        foreach (var tool in customTools)
        {
            if (string.IsNullOrWhiteSpace(tool.OpenApiSpec))
            {
                continue;
            }

            if (McpSandboxConnectionReader.TryReadConnection(tool.OpenApiSpec) is not null)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasPendingSandboxApply(JsonElement statusRoot)
    {
        if (statusRoot.TryGetProperty("overallStatus", out var overallEl)
            && overallEl.ValueKind == JsonValueKind.String
            && string.Equals(overallEl.GetString(), "pending", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsPending(statusRoot, "requirements") || IsPending(statusRoot, "installScripts"))
        {
            return true;
        }

        return false;
    }

    private static bool IsPending(JsonElement statusRoot, string propertyName)
    {
        if (!statusRoot.TryGetProperty(propertyName, out var section) || section.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return section.TryGetProperty("pendingApply", out var pendingEl)
               && pendingEl.ValueKind == JsonValueKind.True;
    }
}
