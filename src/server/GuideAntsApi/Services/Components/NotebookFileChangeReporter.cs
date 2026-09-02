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
    /// Modification is inferred from file size or last-write time only — never from content hashing.
    /// Full reconcile uses the same metadata-first gate (<see cref="NotebookFileHash.IsUnchanged"/>)
    /// and hashes only new/changed/placeholder rows. Do not reintroduce hashing on this path.
    /// </para>
    /// </summary>
    public static async Task<(List<string> NewFiles, List<string> ModifiedFiles)> DetectChangesAsync(
        IServiceProvider serviceProvider,
        string storageRoot,
        InvocationContext context,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
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
            .ToDictionaryAsync(f => f.RelativePath, f => new { f.FileSize, f.LastModifiedUtc }, cancellationToken);

        var syncableRelativePaths = NotebookSyncFileEnumerator.EnumerateSyncableRelativePaths(
            notebookRoot,
            fileNameFilter: f => !NotebookFileIndexingRules.IsTemporaryScriptFile(f));

        var newFiles = new List<string>();
        var modifiedFiles = new List<string>();

        foreach (var dbRelativePath in syncableRelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                // Same durable metadata gate as reconcile (size + whole-second mtime).
                if (dbFile.FileSize != fileSize
                    || NotebookFileHash.ToUtcSecondTicks(dbFile.LastModifiedUtc)
                        != NotebookFileHash.ToUtcSecondTicks(lastModifiedUtc))
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
}
