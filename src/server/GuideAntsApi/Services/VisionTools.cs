using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;
using AntRunner.ToolCalling.AssistantDefinitions;

namespace GuideAntsApi.Services;

/// <summary>
/// Native vision tool: retrieves an image from the current notebook and presents it
/// to the model for visual inspection.
///
/// Delivery: Global Tools row only (user ruling: never auto-injected). The tool
/// returns <see cref="VisionToolResult"/>; the run loop (ThreadRun) composes the
/// calibrated [tool result, synthetic assistant bridge, synthetic user attachment]
/// sequence from the Attachments side channel. Image bytes never enter tool-result
/// text, which keeps the sequence valid for every routed provider.
///
/// Source of truth is the filesystem, not the NotebookFiles table: a script that
/// wrote the image earlier in this same turn is not in the index yet (the index is
/// refreshed by the sync job), so an index lookup would reject a file that exists.
/// Paths are resolved under the notebook root and validated for traversal.
/// </summary>
public static class VisionTools
{
    /// <summary>Hard cap for a single retrieved image. No resizing is performed (no image
    /// library is referenced by the server); oversize files are rejected with an error
    /// tool result rather than silently truncated.</summary>
    internal const long MaxImageBytes = 8L * 1024 * 1024;

    private static IServiceProvider? _provider;
    private static ILogger? _log;

    public static void InitializeServiceProvider(IServiceProvider provider)
    {
        _provider = provider;
        _log = provider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(VisionTools));
    }

    private static readonly HashSet<string> SupportedImageContentTypes =
    [
        "image/png", "image/jpeg", "image/webp", "image/gif"
    ];

    /// <summary>Retrieves an image file from the current notebook for visual inspection.</summary>
    [Tool(
        OperationId = "GetImageForInspection",
        Summary = "Retrieve an image from the current notebook and present it for visual inspection. The image is delivered as an attachment that follows this tool result."
    )]
    [RequiresNotebookContext]
    public static async Task<VisionToolResult> GetImageForInspection(
        [Parameter(Description = "Notebook-relative path of the image file, e.g. 'data/plot.png'. A bare filename is also accepted and resolved against the run's working folder.", Required = true)] string path,
        [Parameter(Description = "One or two sentences describing why this image is being inspected. Shown to the model with the image.")] string? reason = null,
        [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
    {
        if (_provider is null)
        {
            return Error("Vision tool is not initialized.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("path is required.");
        }

        if (context is null)
        {
            return Error("Notebook context is required.");
        }

        using var scope = _provider.CreateScope();

        if (!TryResolveLocalPath(scope.ServiceProvider, context, path, out var physicalPath, out var relativePath, out var failure))
        {
            return Error(failure);
        }

        long size;
        try
        {
            size = new FileInfo(physicalPath).Length;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "VisionTools: failed to stat {Path}", relativePath);
            return Error($"Failed to read '{relativePath}': {ex.Message}");
        }

        if (size > MaxImageBytes)
        {
            return Error($"Image '{relativePath}' is {size} bytes; the maximum is {MaxImageBytes} bytes.");
        }

        var contentType = SniffImageContentType(physicalPath, relativePath);
        if (contentType is null)
        {
            return Error($"'{relativePath}' is not a supported image. Supported types: {string.Join(", ", SupportedImageContentTypes)}.");
        }

        try
        {
            await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 81920, useAsync: true);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            if (bytes.Length > MaxImageBytes)
            {
                return Error($"Image '{relativePath}' is {bytes.Length} bytes; the maximum is {MaxImageBytes} bytes.");
            }

            var caption = string.IsNullOrWhiteSpace(reason)
                ? "Image retrieved from the notebook for inspection."
                : reason.Trim();

            return new VisionToolResult
            {
                Output = JsonSerializer.Serialize(new
                {
                    status = "ok",
                    path = relativePath,
                    contentType,
                    bytes = bytes.Length,
                    presentedAs = "The image is presented in the attached user message that follows this tool result."
                }),
                Attachments =
                [
                    new VisionAttachment(
                        DataUrl: $"data:{contentType};base64,{Convert.ToBase64String(bytes)}",
                        Caption: caption,
                        SourcePath: relativePath,
                        SourceTool: "GetImageForInspection",
                        // No NotebookFileId: the file is read straight from disk and may not be
                        // indexed yet. The attachment persists by relative path, like host-mount rows.
                        NotebookFileId: null)
                ]
            };
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "VisionTools: failed to read {Path}", relativePath);
            return Error($"Failed to read '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a caller-supplied path to a file under the notebook root, without consulting
    /// the NotebookFiles index. Bare filenames are also tried against the run's working
    /// folder (Output/ for private notebooks, Runs/{runId}/ for published ones) because that
    /// is the directory script tools and generated files live in.
    /// </summary>
    internal static bool TryResolveLocalPath(
        IServiceProvider scopedProvider,
        InvocationContext context,
        string path,
        out string physicalPath,
        out string relativePath,
        out string failure)
    {
        physicalPath = string.Empty;
        relativePath = string.Empty;
        failure = string.Empty;

        var resolver = scopedProvider.GetService<IStoragePathResolver>();
        if (resolver is null)
        {
            failure = "Storage path resolver is not available.";
            return false;
        }

        var notebookRoot = resolver.GetNotebookRootPath(context.ProjectId, context.NotebookId);
        var normalized = path.Trim().Replace('\\', '/').TrimStart('/');

        if (normalized.Length == 0)
        {
            failure = "path is required.";
            return false;
        }

        var runFolder = NotebookPathHelper.GetRelativeRunFolder(context);
        var candidates = new List<string> { normalized };

        if (!normalized.StartsWith(runFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add($"{runFolder}/{normalized}");
        }

        foreach (var candidate in candidates)
        {
            if (!NotebookStoragePath.TryResolveUnderRoot(notebookRoot, candidate, out var resolved))
            {
                continue;
            }

            if (!File.Exists(resolved))
            {
                continue;
            }

            physicalPath = resolved;
            relativePath = candidate;
            return true;
        }

        failure = $"No image file found at '{normalized}' in the current notebook (searched the notebook root and '{runFolder}/').";
        return false;
    }

    /// <summary>
    /// Determines the image content type from the file's magic bytes, falling back to the
    /// extension only when the bytes match the type the extension claims. Never returns a
    /// type for a non-image, so a text or binary file cannot be sent as an image part.
    /// </summary>
    private static string? SniffImageContentType(string physicalPath, string relativePath)
    {
        byte[] header;
        try
        {
            using var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            header = new byte[12];
            int read = 0;
            while (read < header.Length)
            {
                int n = fs.Read(header, read, header.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < header.Length)
            {
                Array.Resize(ref header, read);
            }
        }
        catch
        {
            return null;
        }

        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            return "image/png";
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (header.Length >= 6
            && header[0] == 'G' && header[1] == 'I' && header[2] == 'F' && header[3] == '8'
            && (header[4] == '7' || header[4] == '9') && header[5] == 'a')
        {
            return "image/gif";
        }

        if (header.Length >= 12
            && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
            && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
        {
            return "image/webp";
        }

        // Bytes are not a known image; do not trust the extension.
        return null;
    }

    private static VisionToolResult Error(string message) => new()
    {
        Output = JsonSerializer.Serialize(new { error = message }),
        Attachments = []
    };
}
