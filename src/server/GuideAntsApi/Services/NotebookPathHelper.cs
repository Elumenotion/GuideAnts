using System.Security.Cryptography;
using AntRunner.ToolCalling;

namespace GuideAntsApi.Services;

/// <summary>
/// Resolves notebook working directories depending on publication status.
/// For published notebooks each invocation gets its own Runs/{runId} folder (10-char base-62 ID).
/// For private notebooks legacy Output/ is used.
/// </summary>
internal static class NotebookPathHelper
{
    private const int RunIdSize = 10;
    private const string Base62Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static IServiceProvider? _serviceProvider;

    public static void InitializeServiceProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private static IStoragePathResolver? TryGetResolver()
    {
        return _serviceProvider?.GetService<IStoragePathResolver>();
    }

    /// <summary>
    /// Returns the container-mounted path (used by NotebookDockerScriptService).
    /// </summary>
    public static string GetWorkingDirectory(InvocationContext context)
    {
        var resolver = TryGetResolver();
        var basePath = resolver != null
            ? resolver.GetContainerNotebookRootPath(context.ProjectId, context.NotebookId)
            : $"/app/ContentFiles/{context.ProjectId}/notebooks/{context.NotebookId}";
        var relativeFolder = GetRelativeRunFolder(context);
        return $"{basePath}/{relativeFolder}";
    }

    /// <summary>
    /// Returns the local filesystem path (used by Image/Video/Podcast services).
    /// </summary>
    public static string GetLocalWorkingDirectory(InvocationContext context, string storageRoot)
    {
        var resolver = TryGetResolver();
        var basePath = resolver != null
            ? resolver.GetNotebookRootPath(context.ProjectId, context.NotebookId)
            : Path.Combine(storageRoot, context.ProjectId.ToString(), "notebooks", context.NotebookId.ToString());
        var relativeFolder = GetRelativeRunFolder(context);
        return Path.Combine(basePath, relativeFolder).Replace("\\", "/");
    }

    /// <summary>
    /// Returns the relative folder name ("Output" or "Runs/{runId}") for the invocation.
    /// </summary>
    public static string GetRelativeRunFolder(InvocationContext context)
    {
        if (context.IsPublished)
        {
            if (string.IsNullOrEmpty(context.RunId))
            {
                context.RunId = GenerateRunId();
            }
            return $"Runs/{context.RunId}";
        }
        return "Output";
    }

    /// <summary>
    /// Generates a cryptographically random 10-character base-62 identifier.
    /// </summary>
    private static string GenerateRunId()
    {
        Span<byte> bytes = stackalloc byte[RunIdSize];
        RandomNumberGenerator.Fill(bytes);
        
        Span<char> chars = stackalloc char[RunIdSize];
        for (int i = 0; i < RunIdSize; i++)
        {
            chars[i] = Base62Chars[bytes[i] % Base62Chars.Length];
        }
        
        return new string(chars);
    }
}
