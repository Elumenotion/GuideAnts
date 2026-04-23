using AntRunner.ToolCalling;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Detects new and modified files in a notebook directory by comparing filesystem state to database.
/// Returns CWD-relative paths for use in ScriptExecutionResult.NewFiles/ModifiedFiles.
/// </summary>
public static class NotebookFileChangeReporter
{
    /// <summary>
    /// Scans the notebook working directory and returns CWD-relative paths for new and modified files.
    /// Call this BEFORE database sync to detect changes.
    /// </summary>
    public static async Task<(List<string> NewFiles, List<string> ModifiedFiles)> DetectChangesAsync(
        IServiceProvider serviceProvider,
        string storageRoot,
        InvocationContext context,
        ILogger? logger = null)
    {
        var localNotebookPath = NotebookPathHelper.GetLocalWorkingDirectory(context, storageRoot);
        if (!Directory.Exists(localNotebookPath))
            return (new List<string>(), new List<string>());

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dbFiles = await dbContext.NotebookFiles
            .Where(f => f.NotebookId == context.NotebookId)
            .ToDictionaryAsync(f => f.RelativePath, f => new { f.FileSize, f.LastModifiedUtc, f.FileHash });

        var localFiles = Directory.GetFiles(localNotebookPath, "*", SearchOption.AllDirectories)
            .Where(f => !IsTempScriptFile(Path.GetFileName(f)))
            .ToArray();

        var newFiles = new List<string>();
        var modifiedFiles = new List<string>();

        var resolver = scope.ServiceProvider.GetService<IStoragePathResolver>();
        var notebookRoot = resolver != null
            ? resolver.GetNotebookRootPath(context.ProjectId, context.NotebookId)
            : Path.Combine(storageRoot, context.ProjectId.ToString(), "notebooks", context.NotebookId.ToString());

        foreach (var localFile in localFiles)
        {
            // Get path relative to notebook root (for DB comparison)
            var dbRelativePath = Path.GetRelativePath(notebookRoot, localFile).Replace("\\", "/");
            var fileInfo = new FileInfo(localFile);
            var fileSize = fileInfo.Length;
            var lastModifiedUtc = fileInfo.LastWriteTimeUtc;
            var fileHash = ComputeSha256(localFile);

            // Convert to CWD-relative path for the assistant
            var cwdRelativePath = ConvertToCwdRelativePath(dbRelativePath, context.IsPublished, context.RunId);

            if (dbFiles.TryGetValue(dbRelativePath, out var dbFile))
            {
                if (dbFile.FileSize != fileSize || dbFile.LastModifiedUtc != lastModifiedUtc || dbFile.FileHash != fileHash)
                {
                    modifiedFiles.Add(cwdRelativePath);
                }
            }
            else
            {
                newFiles.Add(cwdRelativePath);
            }
        }

        return (newFiles, modifiedFiles);
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
            // File is elsewhere - return as-is or with ../
            if (normalized.Contains('/'))
            {
                return $"../{normalized}";
            }
            return normalized;
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

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}


