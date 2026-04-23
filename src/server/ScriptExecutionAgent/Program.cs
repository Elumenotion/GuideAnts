using CliWrap;
using CliWrap.Buffered;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddLogging();

var app = builder.Build();

// Startup logging: output assembly version so we can verify container bits
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Startup");
var asmVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
startupLogger.LogInformation("ScriptExecutionAgent starting. Assembly version: {Version}", asmVersion);

// Configuration for script execution
var scriptConfig = new ScriptExecutionConfig
{
    MaxScriptSize = 1024 * 1024, // 1MB
    MaxExecutionTime = TimeSpan.FromMinutes(5),
    MaxOutputSize = 1024 * 1024, // 1MB
};

var fileStorageRoot = Environment.GetEnvironmentVariable("FILE_STORAGE_ROOT") ?? throw new InvalidOperationException("FILE_STORAGE_ROOT environment variable is not configured");

// Script execution endpoint
app.MapPost("/execute", async (HttpContext context, ILogger<Program> logger) =>
{
    try
    {
        logger.LogInformation("EXECUTE: Starting request processing");

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync();

        logger.LogInformation("EXECUTE: Read request body, length={Length}", requestBody.Length);
        logger.LogDebug("EXECUTE: Request body content: {RequestBody}", requestBody);

        var request = JsonSerializer.Deserialize<ScriptExecutionRequest>(requestBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (request == null)
        {
            logger.LogError("EXECUTE: JSON deserialization failed - request is null");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Invalid request body - JSON deserialization failed");
            return;
        }

        logger.LogInformation("EXECUTE: JSON deserialization successful - Script length={ScriptLength}, ScriptType={ScriptType}, ProjectId={ProjectId}, WorkingDirectory={WorkingDirectory}",
            request.Script?.Length ?? 0, request.ScriptType, request.ProjectId ?? "NULL", request.WorkingDirectory ?? "NULL");

        logger.LogInformation("EXECUTE: Starting request validation");
        var validationResult = ValidateRequest(request, scriptConfig);
        if (!validationResult.IsValid)
        {
            logger.LogError("EXECUTE: Validation failed - {ErrorMessage}", validationResult.ErrorMessage);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"Validation failed: {validationResult.ErrorMessage}");
            return;
        }

        // Enforce that workingDirectory is inside FILE_STORAGE_ROOT.
        // Project roots may now be slug-based, so do not assume FILE_STORAGE_ROOT/<ProjectId>.
        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            logger.LogWarning("EXECUTE: REJECTED - ProjectId is null or empty");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("ProjectId is required");
            return;
        }

        var fullWorking = Path.GetFullPath(request.WorkingDirectory ?? string.Empty);
        var storageRoot = Path.GetFullPath(fileStorageRoot);

        if (!IsStrictChildOrSamePath(storageRoot, fullWorking))
        {
            logger.LogWarning("EXECUTE: REJECTED - WorkingDirectory '{FullWorking}' is not under storage root '{StorageRoot}'",
                fullWorking, storageRoot);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WorkingDirectory must be under the configured file storage root");
            return;
        }

        logger.LogInformation("Executing script type {ScriptType} in directory {WorkingDirectory}",
            request.ScriptType, request.WorkingDirectory);

        var result = await ExecuteScriptAsync(request, scriptConfig, logger);

        context.Response.ContentType = "application/json";
        var responseJson = JsonSerializer.Serialize(result);
        await context.Response.WriteAsync(responseJson);
    }
    catch (JsonException jsonEx)
    {
        logger.LogError(jsonEx, "EXECUTE: JSON parsing exception - {Message}", jsonEx.Message);
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync($"JSON parsing error: {jsonEx.Message}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "EXECUTE: Unexpected exception during request processing - {Message}", ex.Message);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
    }
});

// Health check endpoint
app.MapGet("/health", () => "OK");

// File listing endpoint
app.MapGet("/files", async (HttpContext context, ILogger<Program> logger) =>
{
    try
    {
        var directory = context.Request.Query["directory"].ToString();
        var projectId = context.Request.Query["projectId"].ToString();
        if (string.IsNullOrEmpty(directory))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Directory parameter is required");
            return;
        }

        if (string.IsNullOrEmpty(projectId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("projectId parameter is required");
            return;
        }

        if (!Directory.Exists(directory))
        {
            await context.Response.WriteAsync("[]");
            return;
        }

        string[] files;
        var storageRoot = Path.GetFullPath(fileStorageRoot);
        var fullDir = Path.GetFullPath(directory);

        if (!IsStrictChildOrSamePath(storageRoot, fullDir))
        {
            logger.LogWarning("/files: REJECTED - Directory '{FullDir}' is not under storage root '{StorageRoot}'", fullDir, storageRoot);
            files = Array.Empty<string>();
        }
        else
        {
            try
            {
                var names = new List<string>();
                foreach (var entry in Directory.GetFileSystemEntries(directory))
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name) || IsTemporaryScriptFile(name)) continue;
                    try
                    {
                        var attr = File.GetAttributes(entry);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }

                    names.Add(name);
                }

                files = names.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "/files: Failed to list directory {Dir}", directory);
                files = Array.Empty<string>();
            }
        }

        var responseJson = JsonSerializer.Serialize(files);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(responseJson);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error listing files");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync($"Error: {ex.Message}");
    }
});

/// <summary>
/// True if <paramref name="path"/> is exactly <paramref name="root"/> or a subdirectory/file under it (path-boundary safe).
/// </summary>
static bool IsStrictChildOrSamePath(string root, string path)
{
    root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.Equals(path, root, StringComparison.Ordinal)) return true;
    var prefix = root + Path.DirectorySeparatorChar;
    return path.StartsWith(prefix, StringComparison.Ordinal);
}

// Helper method to identify temporary script files created by /execute
static bool IsTemporaryScriptFile(string filename)
{
    var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
    return Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase);
}

await app.RunAsync();

static ValidationResult ValidateRequest(ScriptExecutionRequest request, ScriptExecutionConfig config)
{
    if (request.Script.Length > config.MaxScriptSize)
    {
        return ValidationResult.Failure($"Script size {request.Script.Length} exceeds maximum allowed size of {config.MaxScriptSize} bytes");
    }

    if (string.IsNullOrEmpty(request.WorkingDirectory))
    {
        return ValidationResult.Failure("Working directory is required");
    }

    return ValidationResult.Success();
}

static async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, ScriptExecutionConfig config, ILogger logger)
{
    var stdOutBuffer = new StringBuilder();
    var stdErrBuffer = new StringBuilder();
    HashSet<string> preExistingFiles = new(StringComparer.OrdinalIgnoreCase);
    var preSnapshotSucceeded = false;

    try
    {
        if (!Directory.Exists(request.WorkingDirectory))
        {
            Directory.CreateDirectory(request.WorkingDirectory);
            logger.LogInformation("Created working directory: {WorkingDirectory}", request.WorkingDirectory);
        }

        try
        {
            preExistingFiles = Directory
                .EnumerateFiles(request.WorkingDirectory, "*", SearchOption.AllDirectories)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            preSnapshotSucceeded = true;
            logger.LogInformation("Captured {Count} pre-existing files before script execution", preExistingFiles.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to snapshot pre-existing files; zero-byte cleanup will be best-effort");
        }

        var scriptGuid = Guid.NewGuid().ToString("N");
        var scriptFilename = $"{scriptGuid}_{request.ScriptType switch
        {
            ScriptType.Bash => "script.sh",
            ScriptType.PowerShell => "script.ps1",
            ScriptType.Python => "script.py",
            _ => throw new ArgumentOutOfRangeException(nameof(request.ScriptType), request.ScriptType, null)
        }}";

        var scriptFilePath = Path.Combine(request.WorkingDirectory, scriptFilename);

        await File.WriteAllTextAsync(scriptFilePath, request.Script);
        logger.LogInformation("Created script file: {ScriptFilePath}", scriptFilePath);

        var (commandFile, commandArgs) = GetScriptCommand(request.ScriptType, scriptFilePath);

        using var cts = new CancellationTokenSource(config.MaxExecutionTime);
        logger.LogInformation("Executing script: {Command} with args length {ArgCount}", commandFile, commandArgs.Length);

        var run = await Cli.Wrap(commandFile)
            .WithArguments(commandArgs)
            .WithWorkingDirectory(request.WorkingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cts.Token);

        stdOutBuffer.Append(run.StandardOutput);
        stdErrBuffer.Append(run.StandardError);

        if (run.ExitCode != 0)
        {
            logger.LogWarning("Script exited with code {ExitCode}", run.ExitCode);
            stdErrBuffer.AppendLine($"Script exited with code {run.ExitCode}");
        }

        bool preserveScriptForDebug = false;
        try
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            var isDev = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);

            var zeroByteCandidates = new List<string>();
            try
            {
                var allFiles = Directory.EnumerateFiles(request.WorkingDirectory, "*", SearchOption.AllDirectories);
                foreach (var path in allFiles)
                {
                    if (preSnapshotSucceeded && preExistingFiles.Contains(path)) continue;
                    var name = Path.GetFileName(path);
                    if (IsTemporaryScriptFile(name)) continue;
                    long size;
                    try { size = new FileInfo(path).Length; } catch { continue; }
                    if (size == 0) zeroByteCandidates.Add(path);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to scan for zero-byte files prior to script cleanup");
            }

            if (zeroByteCandidates.Count > 0)
            {
                logger.LogWarning("Detected {Count} zero-byte files prior to cleanup in {Dir}", zeroByteCandidates.Count, request.WorkingDirectory);
                if (isDev)
                {
                    preserveScriptForDebug = true;
                    logger.LogInformation("Preserving script file for debugging (Development environment) due to zero-byte artifacts: {ScriptFilePath}", scriptFilePath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pre-cleanup zero-byte detection encountered an error");
        }

        if (!preserveScriptForDebug)
        {
            try
            {
                if (File.Exists(scriptFilePath))
                {
                    File.Delete(scriptFilePath);
                    logger.LogInformation("Cleaned up script file: {ScriptFilePath}", scriptFilePath);
                }
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to clean up script file: {ScriptFilePath}", scriptFilePath);
            }
        }
    }
    catch (OperationCanceledException)
    {
        stdErrBuffer.AppendLine("Script execution timed out");
        logger.LogWarning("Script execution timed out after {Timeout}", config.MaxExecutionTime);
    }
    catch (Exception ex)
    {
        stdErrBuffer.AppendLine($"Error executing script: {ex.Message}");
        logger.LogError(ex, "Error during script execution");
    }

    try
    {
        var removed = new List<string>();
        var allFiles = Directory.EnumerateFiles(request.WorkingDirectory, "*", SearchOption.AllDirectories);
        foreach (var path in allFiles)
        {
            if (preSnapshotSucceeded && preExistingFiles.Contains(path)) continue;
            var name = Path.GetFileName(path);
            if (IsTemporaryScriptFile(name)) continue;

            long size;
            try { size = new FileInfo(path).Length; }
            catch { continue; }

            if (size == 0)
            {
                var rel = Path.GetRelativePath(request.WorkingDirectory, path).Replace("\\", "/");
                try
                {
                    File.Delete(path);
                    removed.Add(rel);
                }
                catch (Exception delEx)
                {
                    logger.LogWarning(delEx, "Failed to delete zero-byte file {Path}", path);
                    removed.Add(rel + " (delete failed)");
                }
            }
        }

        if (removed.Count > 0)
        {
            logger.LogWarning("Removed {Count} zero-byte files created by script in {Dir}", removed.Count, request.WorkingDirectory);
            stdErrBuffer.AppendLine("Warning: The script created zero-byte files which were removed. This usually indicates a failed write or a permissions issue. Please retry the operation.");
            foreach (var rel in removed)
            {
                stdErrBuffer.AppendLine($" - {rel}");
            }
        }
    }
    catch (Exception scanEx)
    {
        logger.LogWarning(scanEx, "Zero-byte file scan/cleanup failed");
    }

    var cleanedOutput = stdOutBuffer.ToString()
        .Replace("\r\n", "\n")
        .Replace("\r", "\n")
        .TrimEnd();

    var cleanedError = stdErrBuffer.ToString()
        .Replace("\r\n", "\n")
        .Replace("\r", "\n")
        .TrimEnd();

    if (cleanedOutput.Length > config.MaxOutputSize)
    {
        cleanedOutput = cleanedOutput.Substring(0, config.MaxOutputSize) + "\n[Output truncated]";
    }

    if (cleanedError.Length > config.MaxOutputSize)
    {
        cleanedError = cleanedError.Substring(0, config.MaxOutputSize) + "\n[Error output truncated]";
    }

    if (string.IsNullOrEmpty(cleanedOutput) && string.IsNullOrEmpty(cleanedError))
    {
        cleanedOutput = "The operation completed successfully";
    }

    return new ScriptExecutionResult
    {
        StandardOutput = cleanedOutput,
        StandardError = cleanedError
    };
}

static (string FileName, string[] Arguments) GetScriptCommand(ScriptType scriptType, string scriptFilePath) => scriptType switch
{
    ScriptType.Bash => ("bash", new[] { scriptFilePath }),
    ScriptType.PowerShell => ("pwsh", new[] { "-File", scriptFilePath }),
    ScriptType.Python => ("python", new[] { scriptFilePath }),
    _ => throw new ArgumentOutOfRangeException(nameof(scriptType), scriptType, null)
};

public class ScriptExecutionRequest
{
    public string Script { get; set; } = string.Empty;
    public ScriptType ScriptType { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
}

public class ScriptExecutionResult
{
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
}

public class ScriptExecutionConfig
{
    public int MaxScriptSize { get; set; } = 1024 * 1024;
    public TimeSpan MaxExecutionTime { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxOutputSize { get; set; } = 1024 * 1024;
}

public class ValidationResult
{
    public bool IsValid { get; private set; }
    public string ErrorMessage { get; private set; } = string.Empty;

    private ValidationResult(bool isValid, string errorMessage = "")
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static ValidationResult Success() => new(true);
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

public enum ScriptType
{
    Bash,
    PowerShell,
    Python
}
