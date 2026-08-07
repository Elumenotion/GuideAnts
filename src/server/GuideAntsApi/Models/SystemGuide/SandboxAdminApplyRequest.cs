using System.Text.Json;

namespace GuideAntsApi.Models.SystemGuide;

public sealed record SandboxAdminApplyRequest(string[]? Targets);

public static class SandboxAdminApplyIntent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string[] ResolveTargets(bool hasScope) =>
        hasScope
            ? new[] { "pip", "installScripts" }
            : new[] { "apt" };

    public static string ResolveForwardBody(string? rawBody, bool hasScope)
    {
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            return rawBody;
        }

        return JsonSerializer.Serialize(
            new SandboxAdminApplyRequest(ResolveTargets(hasScope)),
            JsonOptions);
    }
}
