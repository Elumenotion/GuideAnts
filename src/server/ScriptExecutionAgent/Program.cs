using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ScriptExecutionAgent;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();
var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var asmVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
startupLogger.LogInformation("ScriptExecutionAgent starting. Assembly version: {Version}", asmVersion);

var scriptConfig = new ScriptExecutionConfig
{
    MaxScriptSize = 1024 * 1024,
    MaxExecutionTime = TimeSpan.FromMinutes(5),
    MaxOutputSize = 1024 * 1024
};

var fileStorageRoot = Environment.GetEnvironmentVariable("FILE_STORAGE_ROOT")
    ?? throw new InvalidOperationException("FILE_STORAGE_ROOT environment variable is not configured");
var requireAgentToken = GetBooleanEnvironmentVariable("SCRIPT_EXECUTION_REQUIRE_TOKEN", defaultValue: true);
var allowOwnershipFallback = GetBooleanEnvironmentVariable(
    "SCRIPT_EXECUTION_ALLOW_OWNERSHIP_FALLBACK",
    defaultValue: string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase));
var enableNotebookIdentityIsolation = GetBooleanEnvironmentVariable("SCRIPT_EXECUTION_ENABLE_IDENTITY_ISOLATION", defaultValue: true);
var agentToken = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN");

if (requireAgentToken && string.IsNullOrWhiteSpace(agentToken))
{
    throw new InvalidOperationException("SCRIPT_EXECUTION_AGENT_TOKEN must be configured when SCRIPT_EXECUTION_REQUIRE_TOKEN=true.");
}

startupLogger.LogInformation(
    "SECURITY: startup config tokenRequired={TokenRequired} tokenConfigured={TokenConfigured} storageRootConfigured={StorageRootConfigured} linuxIdentityIsolation={IdentityIsolation} allowOwnershipFallback={AllowOwnershipFallback}",
    requireAgentToken,
    !string.IsNullOrWhiteSpace(agentToken),
    !string.IsNullOrWhiteSpace(fileStorageRoot),
    enableNotebookIdentityIsolation,
    allowOwnershipFallback);

await StartupFilesystemHardening.ApplyAsync(fileStorageRoot, startupLogger);

var securityOptions = new AgentSecurityOptions(
    requireAgentToken,
    agentToken,
    allowOwnershipFallback,
    enableNotebookIdentityIsolation);

app.MapPost("/execute", async (HttpContext context, ILogger<Program> logger) =>
{
    try
    {
        if (!AuthorizeAgentRequest(context, securityOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var request = await JsonSerializer.DeserializeAsync<ScriptExecutionRequest>(
            context.Request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            context.RequestAborted);

        if (request is null)
        {
            logger.LogWarning("SECURITY: /execute rejected because request JSON was missing or invalid.");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid request body");
            return;
        }

        var validationResult = ValidateExecutionRequest(request, scriptConfig);
        if (!validationResult.IsValid)
        {
            logger.LogWarning("SECURITY: /execute rejected due to invalid request. reason={Reason}", LogValueSanitizer.Sanitize(validationResult.ErrorMessage));
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"Validation failed: {validationResult.ErrorMessage}");
            return;
        }

        var projectId = Guid.Parse(request.ProjectId);
        var notebookId = Guid.Parse(request.NotebookId);

        if (!PathGuard.TryResolveAndAuthorizePath(
                fileStorageRoot,
                request.WorkingDirectory,
                projectId,
                notebookId,
                PathAccessMode.Write,
                out var authorizedWorkingDirectory,
                out var notebookRoot,
                out var rejectionReason))
        {
            logger.LogWarning(
                "SECURITY: /execute rejected due to path authorization failure. projectId={ProjectId} notebookId={NotebookId} reason={Reason}",
                projectId,
                notebookId,
                LogValueSanitizer.Sanitize(rejectionReason));
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"WorkingDirectory rejected: {rejectionReason}");
            return;
        }

        logger.LogInformation(
            "Executing script type {ScriptType} in authorized working directory {WorkingDirectory}. projectId={ProjectId} notebookId={NotebookId}",
            request.ScriptType,
            LogValueSanitizer.Sanitize(authorizedWorkingDirectory),
            projectId,
            notebookId);

        var executionIdentity = await NotebookExecutionIdentityProvider.PrepareAsync(
            projectId,
            notebookId,
            notebookRoot,
            authorizedWorkingDirectory,
            securityOptions,
            logger,
            context.RequestAborted);

        var normalizedRequest = request with { WorkingDirectory = authorizedWorkingDirectory };
        var result = await ExecuteScriptAsync(normalizedRequest, scriptConfig, logger, executionIdentity);

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(result));
    }
    catch (JsonException jsonEx)
    {
        logger.LogError(jsonEx, "/execute JSON parsing exception");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync($"JSON parsing error: {jsonEx.Message}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "/execute unexpected exception");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync($"Internal server error: {ex.Message}");
    }
});

app.MapGet("/health", () => "OK");

app.MapGet("/files", async (HttpContext context, ILogger<Program> logger) =>
{
    try
    {
        if (!AuthorizeAgentRequest(context, securityOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var directory = context.Request.Query["directory"].ToString();
        var projectIdValue = context.Request.Query["projectId"].ToString();
        var notebookIdValue = context.Request.Query["notebookId"].ToString();

        if (string.IsNullOrWhiteSpace(directory))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("directory parameter is required");
            return;
        }

        if (!Guid.TryParse(projectIdValue, out var projectId) || projectId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("projectId parameter must be a non-empty GUID");
            return;
        }

        if (!Guid.TryParse(notebookIdValue, out var notebookId) || notebookId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("notebookId parameter must be a non-empty GUID");
            return;
        }

        if (!PathGuard.TryResolveAndAuthorizePath(
                fileStorageRoot,
                directory,
                projectId,
                notebookId,
                PathAccessMode.Read,
                out var authorizedDirectory,
                out var notebookRoot,
                out var rejectionReason))
        {
            logger.LogWarning(
                "SECURITY: /files rejected due to path authorization failure. projectId={ProjectId} notebookId={NotebookId} reason={Reason}",
                projectId,
                notebookId,
                LogValueSanitizer.Sanitize(rejectionReason));
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"directory rejected: {rejectionReason}");
            return;
        }

        if (!Directory.Exists(authorizedDirectory))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
            return;
        }

        var executionIdentity = await NotebookExecutionIdentityProvider.PrepareAsync(
            projectId,
            notebookId,
            notebookRoot,
            authorizedDirectory,
            securityOptions,
            logger,
            context.RequestAborted);

        var files = await ListFilesAsync(authorizedDirectory, executionIdentity, securityOptions, logger, context.RequestAborted);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(files));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error listing files");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync($"Error: {ex.Message}");
    }
});

await app.RunAsync();

static bool AuthorizeAgentRequest(HttpContext context, AgentSecurityOptions options, ILogger logger)
{
    if (!options.RequireAgentToken)
    {
        return true;
    }

    var suppliedToken = context.Request.Headers["X-Script-Agent-Token"].ToString();
    if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrWhiteSpace(options.AgentToken))
    {
        logger.LogWarning("SECURITY: agent token missing. path={Path}", LogValueSanitizer.Sanitize(context.Request.Path.Value));
        return false;
    }

    if (!string.Equals(suppliedToken, options.AgentToken, StringComparison.Ordinal))
    {
        logger.LogWarning("SECURITY: agent token mismatch. path={Path}", LogValueSanitizer.Sanitize(context.Request.Path.Value));
        return false;
    }

    return true;
}

static async Task<string[]> ListFilesAsync(
    string authorizedDirectory,
    NotebookExecutionIdentity? executionIdentity,
    AgentSecurityOptions securityOptions,
    ILogger logger,
    CancellationToken cancellationToken)
{
    List<string> entries;
    if (executionIdentity is not null && OperatingSystem.IsLinux() && securityOptions.EnableNotebookIdentityIsolation)
    {
        try
        {
            entries = await ListFilesViaSetprivAsync(authorizedDirectory, executionIdentity, cancellationToken);
        }
        catch (Exception ex)
        {
            if (!securityOptions.AllowOwnershipFallback)
            {
                throw;
            }

            logger.LogWarning(ex, "SECURITY: setpriv listing failed for {Directory}. Falling back to direct listing.", LogValueSanitizer.Sanitize(authorizedDirectory));
            entries = Directory.GetFileSystemEntries(authorizedDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToList();
        }
    }
    else
    {
        entries = Directory.GetFileSystemEntries(authorizedDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToList();
    }

    var names = new List<string>();
    foreach (var name in entries)
    {
        if (IsTemporaryScriptFile(name))
        {
            continue;
        }

        var fullEntry = Path.Combine(authorizedDirectory, name);
        try
        {
            var attr = File.GetAttributes(fullEntry);
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                logger.LogWarning("SECURITY: skipping reparse-point entry during listing. entry={Entry}", LogValueSanitizer.Sanitize(fullEntry));
                continue;
            }
        }
        catch
        {
            continue;
        }

        names.Add(name);
    }

    return names.ToArray();
}

static async Task<List<string>> ListFilesViaSetprivAsync(
    string authorizedDirectory,
    NotebookExecutionIdentity executionIdentity,
    CancellationToken cancellationToken)
{
    var run = await Cli.Wrap("setpriv")
        .WithArguments(args => args
            .Add("--reuid")
            .Add(executionIdentity.Uid.ToString())
            .Add("--regid")
            .Add(executionIdentity.Gid.ToString())
            .Add("--init-groups")
            .Add("--no-new-privs")
            .Add("--bounding-set")
            .Add("-all")
            .Add("--")
            .Add("ls")
            .Add("-1A")
            .Add("--")
            .Add(authorizedDirectory))
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(cancellationToken);

    if (run.ExitCode != 0)
    {
        throw new InvalidOperationException($"setpriv listing failed with exit code {run.ExitCode}: {run.StandardError}");
    }

    return run.StandardOutput
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToList();
}

static ValidationResult ValidateExecutionRequest(ScriptExecutionRequest request, ScriptExecutionConfig config)
{
    if (string.IsNullOrWhiteSpace(request.Script))
    {
        return ValidationResult.Failure("Script is required");
    }

    if (request.Script.Length > config.MaxScriptSize)
    {
        return ValidationResult.Failure($"Script size {request.Script.Length} exceeds maximum allowed size of {config.MaxScriptSize} bytes");
    }

    if (!Enum.IsDefined(typeof(ScriptType), request.ScriptType))
    {
        return ValidationResult.Failure("ScriptType is invalid");
    }

    if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
    {
        return ValidationResult.Failure("WorkingDirectory is required");
    }

    if (!Guid.TryParse(request.ProjectId, out var projectId) || projectId == Guid.Empty)
    {
        return ValidationResult.Failure("ProjectId must be a non-empty GUID");
    }

    if (!Guid.TryParse(request.NotebookId, out var notebookId) || notebookId == Guid.Empty)
    {
        return ValidationResult.Failure("NotebookId must be a non-empty GUID");
    }

    return ValidationResult.Success();
}

static bool GetBooleanEnvironmentVariable(string name, bool defaultValue)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return defaultValue;
    }

    return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
}

static bool IsTemporaryScriptFile(string filename)
{
    var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
    return Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase);
}

static async Task<ScriptExecutionResult> ExecuteScriptAsync(
    ScriptExecutionRequest request,
    ScriptExecutionConfig config,
    ILogger logger,
    NotebookExecutionIdentity? executionIdentity)
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
            logger.LogInformation("Created working directory: {WorkingDirectory}", LogValueSanitizer.Sanitize(request.WorkingDirectory));
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

        if (executionIdentity is not null && OperatingSystem.IsLinux())
        {
            try
            {
                await NotebookExecutionIdentityProvider.PrepareScriptFileAsync(scriptFilePath, executionIdentity, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SECURITY: failed to apply notebook identity ownership to script file {ScriptFilePath}", LogValueSanitizer.Sanitize(scriptFilePath));
            }
        }

        var (commandFile, commandArgs) = GetScriptCommand(request.ScriptType, scriptFilePath);
        using var cts = new CancellationTokenSource(config.MaxExecutionTime);
        BufferedCommandResult run;
        if (executionIdentity is not null && OperatingSystem.IsLinux())
        {
            run = await ExecuteScriptWithSetprivAsync(commandFile, commandArgs, request.WorkingDirectory, executionIdentity, cts.Token);
        }
        else
        {
            run = await Cli.Wrap(commandFile)
                .WithArguments(commandArgs)
                .WithWorkingDirectory(request.WorkingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cts.Token);
        }

        stdOutBuffer.Append(run.StandardOutput);
        stdErrBuffer.Append(run.StandardError);
        if (run.ExitCode != 0)
        {
            stdErrBuffer.AppendLine($"Script exited with code {run.ExitCode}");
        }

        var preserveScriptForDebug = false;
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var allFiles = Directory.EnumerateFiles(request.WorkingDirectory, "*", SearchOption.AllDirectories);
                preserveScriptForDebug = allFiles
                    .Where(path => !preSnapshotSucceeded || !preExistingFiles.Contains(path))
                    .Where(path => !IsTemporaryScriptFile(Path.GetFileName(path)))
                    .Any(path => new FileInfo(path).Length == 0);
            }
            catch
            {
                preserveScriptForDebug = false;
            }
        }

        if (!preserveScriptForDebug && File.Exists(scriptFilePath))
        {
            try
            {
                File.Delete(scriptFilePath);
            }
            catch (Exception cleanupEx)
            {
                logger.LogWarning(cleanupEx, "Failed to clean up script file: {ScriptFilePath}", LogValueSanitizer.Sanitize(scriptFilePath));
            }
        }
    }
    catch (OperationCanceledException)
    {
        stdErrBuffer.AppendLine("Script execution timed out");
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
            try { size = new FileInfo(path).Length; } catch { continue; }
            if (size != 0) continue;

            var rel = Path.GetRelativePath(request.WorkingDirectory, path).Replace("\\", "/");
            try
            {
                File.Delete(path);
                removed.Add(rel);
            }
            catch
            {
                removed.Add(rel + " (delete failed)");
            }
        }

        if (removed.Count > 0)
        {
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
        cleanedOutput = cleanedOutput[..config.MaxOutputSize] + "\n[Output truncated]";
    }

    if (cleanedError.Length > config.MaxOutputSize)
    {
        cleanedError = cleanedError[..config.MaxOutputSize] + "\n[Error output truncated]";
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

static async Task<BufferedCommandResult> ExecuteScriptWithSetprivAsync(
    string commandFile,
    string[] commandArgs,
    string workingDirectory,
    NotebookExecutionIdentity executionIdentity,
    CancellationToken cancellationToken)
{
    return await Cli.Wrap("setpriv")
        .WithArguments(args =>
        {
            args
                .Add("--reuid")
                .Add(executionIdentity.Uid.ToString())
                .Add("--regid")
                .Add(executionIdentity.Gid.ToString())
                .Add("--init-groups")
                .Add("--no-new-privs")
                .Add("--bounding-set")
                .Add("-all")
                .Add("--")
                .Add(commandFile);

            foreach (var commandArg in commandArgs)
            {
                args.Add(commandArg);
            }
        })
        .WithWorkingDirectory(workingDirectory)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(cancellationToken);
}

static (string FileName, string[] Arguments) GetScriptCommand(ScriptType scriptType, string scriptFilePath) => scriptType switch
{
    ScriptType.Bash => ("bash", new[] { scriptFilePath }),
    ScriptType.PowerShell => ("pwsh", new[] { "-File", scriptFilePath }),
    ScriptType.Python => ("python", new[] { scriptFilePath }),
    _ => throw new ArgumentOutOfRangeException(nameof(scriptType), scriptType, null)
};

file sealed record AgentSecurityOptions(
    bool RequireAgentToken,
    string? AgentToken,
    bool AllowOwnershipFallback,
    bool EnableNotebookIdentityIsolation);

file sealed record NotebookExecutionIdentity(string UserName, string GroupName, int Uid, int Gid);

file static class StartupFilesystemHardening
{
    public static async Task ApplyAsync(string fileStorageRoot, ILogger logger)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fullStorageRoot = Path.GetFullPath(fileStorageRoot);
        await BestEffortCommandAsync("chmod", new[] { "751", fullStorageRoot }, logger, "chmod FILE_STORAGE_ROOT");
        await BestEffortCommandAsync("chown", new[] { "-R", "root:root", "/app/script-agent" }, logger, "chown script-agent");
        await BestEffortCommandAsync("chmod", new[] { "-R", "go-rwx", "/app/script-agent" }, logger, "chmod script-agent");
    }

    private static async Task BestEffortCommandAsync(string fileName, IReadOnlyCollection<string> args, ILogger logger, string description)
    {
        try
        {
            var result = await Cli.Wrap(fileName)
                .WithArguments(builder =>
                {
                    foreach (var arg in args)
                    {
                        builder.Add(arg);
                    }
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode != 0)
            {
                logger.LogWarning("SECURITY: startup hardening command failed ({Description}). exitCode={ExitCode} stderr={StdErr}", LogValueSanitizer.Sanitize(description), result.ExitCode, LogValueSanitizer.Sanitize(result.StandardError));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SECURITY: startup hardening command threw ({Description}).", LogValueSanitizer.Sanitize(description));
        }
    }
}

file static class NotebookExecutionIdentityProvider
{
    private static readonly ConcurrentDictionary<string, NotebookExecutionIdentity> IdentityCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IdentityLocks = new(StringComparer.Ordinal);

    public static async Task<NotebookExecutionIdentity?> PrepareAsync(
        Guid projectId,
        Guid notebookId,
        string notebookRoot,
        string authorizedWorkingDirectory,
        AgentSecurityOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !options.EnableNotebookIdentityIsolation)
        {
            return null;
        }

        var (mountRegistry, _, _) = NotebookMountsRegistry.TryLoad(notebookRoot);
        mountRegistry ??= NotebookMountsRegistry.Empty;

        if (mountRegistry.Mounts.Count > 0)
        {
            logger.LogInformation(
                "SECURITY: notebook has registered mounts; using compatibility execution mode. projectId={ProjectId} notebookId={NotebookId} mountCount={MountCount}",
                projectId,
                notebookId,
                mountRegistry.Mounts.Count);
            return null;
        }

        var identity = await GetOrCreateIdentityAsync(projectId, notebookId, logger, cancellationToken);

        try
        {
            await EnsureOwnedAndRestrictedAsync(notebookRoot, identity, mountRegistry, cancellationToken);
            await EnsureOwnedAndRestrictedAsync(authorizedWorkingDirectory, identity, mountRegistry, cancellationToken);
        }
        catch (Exception ex)
        {
            if (!options.AllowOwnershipFallback)
            {
                throw new InvalidOperationException($"Notebook permission preparation failed for notebook {notebookId}.", ex);
            }

            logger.LogWarning(ex, "SECURITY: notebook ownership/permission prep failed; running in compatibility mode. notebookId={NotebookId}", notebookId);
        }

        logger.LogInformation(
            "SECURITY: execution identity resolved. projectId={ProjectId} notebookId={NotebookId} user={User} uid={Uid} gid={Gid}",
            projectId, notebookId, identity.UserName, identity.Uid, identity.Gid);
        return identity;
    }

    public static async Task PrepareScriptFileAsync(string scriptFilePath, NotebookExecutionIdentity identity, CancellationToken cancellationToken)
    {
        await RunCommandAsync("chown", new[] { $"{identity.Uid}:{identity.Gid}", scriptFilePath }, cancellationToken);
        await RunCommandAsync("chmod", new[] { "700", scriptFilePath }, cancellationToken);
    }

    private static async Task<NotebookExecutionIdentity> GetOrCreateIdentityAsync(
        Guid projectId,
        Guid notebookId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{projectId:D}:{notebookId:D}";
        if (IdentityCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var identityLock = IdentityLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await identityLock.WaitAsync(cancellationToken);
        try
        {
            if (IdentityCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            var suffix = notebookId.ToString("N")[..12];
            var userName = $"nb_{suffix}";
            var groupName = $"nbg_{suffix}";

            if (!await GroupExistsAsync(groupName, cancellationToken))
            {
                await RunCommandAsync("groupadd", new[] { "--system", groupName }, cancellationToken, tolerateAlreadyExists: true);
            }

            if (!await UserExistsAsync(userName, cancellationToken))
            {
                await RunCommandAsync(
                    "useradd",
                    new[] { "--system", "--gid", groupName, "--home", "/nonexistent", "--shell", "/usr/sbin/nologin", userName },
                    cancellationToken,
                    tolerateAlreadyExists: true);
            }

            var uid = await ReadNumericCommandOutputAsync("id", new[] { "-u", userName }, cancellationToken);
            var gid = await ReadNumericCommandOutputAsync("id", new[] { "-g", userName }, cancellationToken);
            await EnsureSetprivReadyAsync(uid, gid, cancellationToken);

            var created = new NotebookExecutionIdentity(userName, groupName, uid, gid);
            IdentityCache[cacheKey] = created;
            logger.LogInformation("SECURITY: created notebook execution identity cache entry for {CacheKey}", cacheKey);
            return created;
        }
        finally
        {
            identityLock.Release();
        }
    }

    private static async Task EnsureSetprivReadyAsync(int uid, int gid, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await Cli.Wrap("setpriv")
                .WithArguments(args => args
                    .Add("--reuid")
                    .Add(uid.ToString())
                    .Add("--regid")
                    .Add(gid.ToString())
                    .Add("--init-groups")
                    .Add("--")
                    .Add("true"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (result.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw new InvalidOperationException($"setpriv identity warm-up failed for uid={uid} gid={gid}");
    }

    private static async Task EnsureOwnedAndRestrictedAsync(
        string path,
        NotebookExecutionIdentity identity,
        NotebookMountsRegistry mountRegistry,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if (mountRegistry.IsUnderAnyContainerSourcePath(path))
        {
            return;
        }

        // -P: never traverse symlinks (registered mount links stay link-only; host trees are not walked).
        await RunCommandAsync("chown", new[] { "-R", "-P", $"{identity.Uid}:{identity.Gid}", path }, cancellationToken);
        await RunCommandAsync("chmod", new[] { "700", path }, cancellationToken);
    }

    private static async Task<bool> GroupExistsAsync(string groupName, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("getent")
            .WithArguments(args => args.Add("group").Add(groupName))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task<bool> UserExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap("id")
            .WithArguments(args => args.Add("-u").Add(userName))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task<int> ReadNumericCommandOutputAsync(string command, IReadOnlyCollection<string> args, CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(builder =>
            {
                foreach (var arg in args)
                {
                    builder.Add(arg);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{command}' failed: {result.StandardError}");
        }

        if (!int.TryParse(result.StandardOutput.Trim(), out var parsed))
        {
            throw new InvalidOperationException($"Command '{command}' returned non-numeric output: {result.StandardOutput}");
        }

        return parsed;
    }

    private static async Task RunCommandAsync(
        string command,
        IReadOnlyCollection<string> args,
        CancellationToken cancellationToken,
        bool tolerateAlreadyExists = false)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(builder =>
            {
                foreach (var arg in args)
                {
                    builder.Add(arg);
                }
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var stderr = result.StandardError ?? string.Empty;
        if (tolerateAlreadyExists &&
            (stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("is not unique", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new InvalidOperationException($"Command '{command}' failed with exit code {result.ExitCode}: {stderr}");
    }
}

public sealed record ScriptExecutionRequest
{
    public string Script { get; init; } = string.Empty;
    public ScriptType ScriptType { get; init; }
    public string WorkingDirectory { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string NotebookId { get; init; } = string.Empty;
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

/// <summary>
/// Entry-point marker for in-process test hosting (WebApplicationFactory).
/// </summary>
public partial class Program;
