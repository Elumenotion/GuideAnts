namespace GuideAntsApi.Services.LlamaCpp;

/// <summary>
/// Maps container paths under <c>/models-local/llama/...</c> to host paths under the
/// configured model store directory (the llama subtree of the single
/// <c>ai_local_models</c> named volume).
/// </summary>
public static class LlamaModelStorePathResolver
{
    // Container-side path to the llama model tree. Must match
    // GA_LLAMA_MODEL_DIR set in docker-compose.yml for guideants-ai.
    private const string ContainerPrefix = "/models-local/llama/";
    private const string ContainerRoot = "/models-local/llama";

    public static string ResolveHostPath(string modelStoreRootFullPath, string containerPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath))
        {
            return string.Empty;
        }

        var normalized = containerPath.Trim().Replace('\\', '/');

        // Strip the known container prefix if present. Paths already
        // relative to the model store, or from unfamiliar prefixes, are
        // treated as store-relative — this is intentional resilience so
        // a mis-typed /Models/... or bare "MyAlias/..." still resolves.
        string relative;
        if (normalized.StartsWith(ContainerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            relative = normalized[ContainerPrefix.Length..];
        }
        else if (normalized.Equals(ContainerRoot, StringComparison.OrdinalIgnoreCase))
        {
            relative = string.Empty;
        }
        else
        {
            relative = normalized.TrimStart('/');
        }

        relative = relative.TrimStart('/');
        if (relative.Length == 0)
        {
            return Path.GetFullPath(modelStoreRootFullPath);
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = modelStoreRootFullPath;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return Path.GetFullPath(path);
    }

    public static string ToContainerPath(string relativeUnderModels)
    {
        var trimmed = relativeUnderModels.Trim().Replace('\\', '/').TrimStart('/');
        return ContainerPrefix + trimmed;
    }
}
