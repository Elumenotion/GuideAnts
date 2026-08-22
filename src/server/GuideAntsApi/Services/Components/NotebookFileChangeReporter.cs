using AntRunner.Chat;
using AntRunner.ToolCalling;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Components.Sync;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Detects new and modified files in a notebook directory by comparing filesystem state to database.
/// Returns CWD-relative paths for use in ScriptExecutionResult.NewFiles/ModifiedFiles.
/// </summary>
public static class NotebookFileChangeReporter
{
    /// <summary>
    /// Scans the notebook root (same rules as sync indexing) and returns CWD-relative paths for
    /// new and modified files. Call this BEFORE database sync to surface change hints to the assistant.
    /// <para>
    /// This is a best-effort, metadata-only heuristic: modification is inferred from a difference in
    /// file size or last-write time, NOT from content hashing. It intentionally trades precision for
    /// speed so the post-execution hot path never opens or hashes file contents. The authoritative
    /// reconciliation in <see cref="NotebookFileSyncService"/> still hashes each file (SHA-256) and is
    /// the source of truth; a content change that preserves both size and timestamp will be caught by
    /// that sync even though it is not reported here. Do not reintroduce hashing on this path.
    /// </para>
    /// </summary>
    public static async Task<(List<string> NewFiles, List<string> ModifiedFiles)> DetectChangesAsync(
        IServiceProvider serviceProvider,
        string storageRoot,
        InvocationContext context,
        ILogger? logger = null)
    {
        using var scope = serviceProvider.CreateScope();
        var resolver = scope.ServiceProvider.GetService<IStoragePathResolver>();
        var notebookRoot = resolver != null
            ? resolver.GetNotebookRootPath(context.ProjectId, context.NotebookId)
            : Path.Combine(storageRoot, context.ProjectId.ToString(), "notebooks", context.NotebookId.ToString());

        if (!Directory.Exists(notebookRoot))
            return (new List<string>(), new List<string>());

        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dbFiles = await dbContext.NotebookFiles
            .Where(f => f.NotebookId == context.NotebookId)
            .ToDictionaryAsync(f => f.RelativePath, f => new { f.FileSize, f.LastModifiedUtc });

        var syncableRelativePaths = NotebookSyncFileEnumerator.EnumerateSyncableRelativePaths(
            notebookRoot,
            fileNameFilter: f => !IsTempScriptFile(f) && !NotebookFileIndexingRules.IsTemporaryScriptFile(f));

        var newFiles = new List<string>();
        var modifiedFiles = new List<string>();

        foreach (var dbRelativePath in syncableRelativePaths)
        {
            var localFile = Path.Combine(notebookRoot, dbRelativePath.Replace('/', Path.DirectorySeparatorChar));

            long fileSize;
            DateTime lastModifiedUtc;
            try
            {
                var fileInfo = new FileInfo(localFile);
                if (!fileInfo.Exists)
                {
                    // File vanished between enumeration and inspection (e.g. a transient script temp file).
                    continue;
                }

                fileSize = fileInfo.Length;
                lastModifiedUtc = fileInfo.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort reporting: a single unreadable/locked file must not abort the whole report.
                logger?.LogDebug(ex, "Skipping file during change reporting due to access error: {Path}", dbRelativePath);
                continue;
            }

            // Convert to CWD-relative path for the assistant
            var cwdRelativePath = ConvertToCwdRelativePath(dbRelativePath, context.IsPublished, context.RunId);

            if (dbFiles.TryGetValue(dbRelativePath, out var dbFile))
            {
                if (dbFile.FileSize != fileSize || dbFile.LastModifiedUtc != lastModifiedUtc)
                {
                    modifiedFiles.Add(cwdRelativePath);
                }
            }
            else
            {
                newFiles.Add(cwdRelativePath);
            }
        }

        logger?.LogDebug(
            "Notebook {NotebookId} change report: {NewCount} new, {ModifiedCount} modified (of {ScannedCount} syncable paths)",
            context.NotebookId,
            newFiles.Count,
            modifiedFiles.Count,
            syncableRelativePaths.Count);

        return (newFiles, modifiedFiles);
    }

    /// <summary>
    /// Collects DB-relative paths from turn output for fast register.
    /// Excludes tooling/artifact paths that sync will not index.
    /// </summary>
    public static IReadOnlyList<string> GetDbRelativePaths(ChatRunOutput? output, bool isPublished, string? runId)
    {
        var paths = NotebookPathResolver.GetDbRelativePaths(output, isPublished, runId);
        return paths
            .Where(p => !NotebookArtifactPathExclusions.IsExcludedRelativePath(p))
            .ToList();
    }

    /// <summary>
    /// Converts a specific file's relative path to CWD-relative format.
    /// Use this when you know exactly which file was created (e.g., image generation).
    /// </summary>
    public static string ToCwdRelativePath(string dbRelativePath, bool isPublished, string? runId)
    {
        return ConvertToCwdRelativePath(dbRelativePath, isPublished, runId);
    }

    /// <summary>
    /// Converts a DB-relative path (e.g., "Output/foo.png" or "Runs/{runId}/foo.png") to CWD-relative.
    /// - Private notebooks (CWD = Output/): "Output/foo.png" → "foo.png"
    /// - Published guides (CWD = Runs/{runId}/): "Runs/{runId}/foo.png" → "foo.png", other paths → "../{actualPath}"
    /// </summary>
    private static string ConvertToCwdRelativePath(string dbRelativePath, bool isPublished, string? runId)
    {
        if (string.IsNullOrWhiteSpace(dbRelativePath))
            return dbRelativePath;

        var normalized = dbRelativePath.Replace('\\', '/').Trim().TrimStart('/');

        if (isPublished && !string.IsNullOrWhiteSpace(runId))
        {
            // Published guide: CWD is Runs/{runId}/
            var runPrefix = $"Runs/{runId}/";
            if (normalized.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // File is in current run folder - just return filename portion
                return normalized.Substring(runPrefix.Length);
            }
            // File is elsewhere - return relative path from Runs/{runId}/
            // e.g., "Output/foo.png" → "../Output/foo.png"
            return $"../{normalized}";
        }
        else
        {
            // Private notebook: CWD is Output/
            const string outputPrefix = "Output/";
            if (normalized.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(outputPrefix.Length);
            }

            // Notebook-root or other non-Output paths are addressed from Output/ via ../
            return $"../{normalized}";
        }
    }

    /// <summary>
    /// Checks if a filename is a temporary script file that should be excluded from file change reporting.
    /// Matches patterns like: {guid}_script.py, {guid}_script.sh, {guid}_script.ps1, script_{guid}.py, etc.
    /// </summary>
    private static bool IsTempScriptFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        // Pattern 1: script_{something}.ext (e.g., "script_test.py")
        if (fileName.StartsWith("script_", StringComparison.OrdinalIgnoreCase))
            return true;

        // Pattern 2: {guid}_script.ext (e.g., "4f02928666d64d09ad265f2f52c96309_script.py")
        // Look for "_script." in the filename
        var lowerName = fileName.ToLowerInvariant();
        if (lowerName.Contains("_script."))
        {
            // Check if the part before _script looks like a guid (32 hex chars or with hyphens)
            var underscoreIdx = lowerName.IndexOf("_script.", StringComparison.Ordinal);
            if (underscoreIdx > 0)
            {
                var prefix = fileName.Substring(0, underscoreIdx);
                // Check if it's a GUID (with or without hyphens)
                if (Guid.TryParse(prefix, out _) ||
                    (prefix.Length == 32 && prefix.All(c => char.IsAsciiHexDigit(c))))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
