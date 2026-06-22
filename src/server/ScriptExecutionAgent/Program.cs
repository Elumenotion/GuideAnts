using System.Collections.Concurrent;
using System.Security.Cryptography;
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
var adminApiEnabled = GetBooleanEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_API_ENABLED", defaultValue: false);
var adminToken = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_TOKEN");
var adminStateDir = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_STATE_DIR");
if (string.IsNullOrWhiteSpace(adminStateDir))
{
    adminStateDir = OperatingSystem.IsWindows()
        ? Path.Combine(fileStorageRoot, ".guideants", "script-agent-admin")
        : "/var/lib/guideants/script-agent-admin";
}
var adminOptions = new AdminApiOptions(
    adminApiEnabled,
    adminToken,
    Path.GetFullPath(adminStateDir),
    GetBooleanEnvironmentVariable("SCRIPT_EXECUTION_ADMIN_FAIL_OPEN", defaultValue: false));
var scopeStateRoot = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_SCOPE_STATE_ROOT");
if (string.IsNullOrWhiteSpace(scopeStateRoot))
{
    scopeStateRoot = Path.Combine(fileStorageRoot, ".guideants", "script-execution");
}
var requireScopedPythonVenv = GetBooleanEnvironmentVariable(
    "SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV",
    defaultValue: OperatingSystem.IsLinux());
var basePythonVenvPath = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_BASE_PYTHON_VENV");
if (string.IsNullOrWhiteSpace(basePythonVenvPath) && OperatingSystem.IsLinux())
{
    basePythonVenvPath = "/opt/venv";
}
var scopeOptions = new ScriptExecutionScopeOptions(
    Path.GetFullPath(scopeStateRoot),
    Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_SCOPE_PYTHON_VENV_DIR"),
    Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_PYTHON_BOOTSTRAP"),
    requireScopedPythonVenv,
    string.IsNullOrWhiteSpace(basePythonVenvPath) ? null : Path.GetFullPath(basePythonVenvPath));

if (requireAgentToken && string.IsNullOrWhiteSpace(agentToken))
{
    throw new InvalidOperationException("SCRIPT_EXECUTION_AGENT_TOKEN must be configured when SCRIPT_EXECUTION_REQUIRE_TOKEN=true.");
}

if (adminOptions.Enabled && string.IsNullOrWhiteSpace(adminOptions.AdminToken))
{
    throw new InvalidOperationException("SCRIPT_EXECUTION_ADMIN_TOKEN must be configured when SCRIPT_EXECUTION_ADMIN_API_ENABLED=true.");
}

startupLogger.LogInformation(
    "SECURITY: startup config tokenRequired={TokenRequired} tokenConfigured={TokenConfigured} storageRootConfigured={StorageRootConfigured} linuxIdentityIsolation={IdentityIsolation} allowOwnershipFallback={AllowOwnershipFallback} scopeStateRoot={ScopeStateRoot} requireScopedVenv={RequireScopedVenv} adminApiEnabled={AdminApiEnabled} adminTokenConfigured={AdminTokenConfigured} adminStateDir={AdminStateDir}",
    requireAgentToken,
    !string.IsNullOrWhiteSpace(agentToken),
    !string.IsNullOrWhiteSpace(fileStorageRoot),
    enableNotebookIdentityIsolation,
    allowOwnershipFallback,
    LogValueSanitizer.Sanitize(scopeOptions.StateRootPath),
    scopeOptions.RequireScopedPythonVenv,
    adminOptions.Enabled,
    !string.IsNullOrWhiteSpace(adminOptions.AdminToken),
    LogValueSanitizer.Sanitize(adminOptions.StateDirectoryPath));

await StartupFilesystemHardening.ApplyAsync(fileStorageRoot, startupLogger);
Directory.CreateDirectory(scopeOptions.StateRootPath);
if (adminOptions.Enabled)
{
    await AdminStateRuntime.InitializeAsync(adminOptions, scopeOptions, startupLogger, CancellationToken.None);
}

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
        var result = await ExecuteScriptAsync(normalizedRequest, scriptConfig, logger, executionIdentity, scopeOptions, adminOptions);

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

if (adminOptions.Enabled)
{
    var adminApiJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    app.MapGet("/admin/health", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = "OK",
            adminStateDir = adminOptions.StateDirectoryPath,
            scopeStateRoot = scopeOptions.StateRootPath
        }));
    });

    app.MapGet("/admin/requirements", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var requirementsPath = TryResolveAdminScope(context, scopeOptions, out var scope, out var error)
            ? AdminStateRuntime.GetRequirementsPath(adminOptions, scope)
            : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
        if (!string.IsNullOrEmpty(error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(error);
            return;
        }

        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(File.Exists(requirementsPath) ? await File.ReadAllTextAsync(requirementsPath, context.RequestAborted) : string.Empty);
    });

    app.MapPut("/admin/requirements", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var body = await ReadRequestBodyAsync(context);
        var validation = ScriptExecutionScopeRuntime.ValidateRequirements(body);
        if (!validation.IsValid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(validation.ErrorMessage);
            return;
        }

        var hasScope = TryResolveAdminScope(context, scopeOptions, out var scope, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(error);
            return;
        }

        var requirementsPath = hasScope
            ? AdminStateRuntime.GetRequirementsPath(adminOptions, scope)
            : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
        await AtomicFile.WriteAllTextAsync(requirementsPath, body, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    });

    app.MapGet("/admin/apt-packages", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var aptPackagesPath = AdminStateRuntime.GetAptPackagesPath(adminOptions);
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync(File.Exists(aptPackagesPath) ? await File.ReadAllTextAsync(aptPackagesPath, context.RequestAborted) : string.Empty);
    });

    app.MapPut("/admin/apt-packages", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var body = await ReadRequestBodyAsync(context);
        var validation = AdminStateRuntime.ValidateAptPackages(body);
        if (!validation.IsValid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(validation.ErrorMessage);
            return;
        }

        await AtomicFile.WriteAllTextAsync(AdminStateRuntime.GetAptPackagesPath(adminOptions), body, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    });

    app.MapGet("/admin/setup-status", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var hasScope = TryResolveAdminScope(context, scopeOptions, out var scope, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(error);
            return;
        }

        var status = await AdminSetupStatusRuntime.BuildAsync(
            hasScope,
            hasScope ? scope : null,
            scopeOptions,
            adminOptions,
            context.RequestAborted);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(status, adminApiJsonOptions));
    });

    app.MapGet("/admin/install-scripts", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        if (!TryResolveAdminScope(context, scopeOptions, out var scope, out var error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(string.IsNullOrEmpty(error)
                ? "projectId and guideId are required for install scripts."
                : error);
            return;
        }

        var document = AdminInstallScriptsRuntime.ReadDocument(scope);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(document, adminApiJsonOptions));
    });

    app.MapPut("/admin/install-scripts", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        if (!TryResolveAdminScope(context, scopeOptions, out var scope, out var error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(string.IsNullOrEmpty(error)
                ? "projectId and guideId are required for install scripts."
                : error);
            return;
        }

        var body = await ReadRequestBodyAsync(context);
        try
        {
            var document = await AdminInstallScriptsRuntime.ParseAndValidateSubmitAsync(body, context.RequestAborted);
            await AdminInstallScriptsRuntime.PersistDocumentAsync(scope, document, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (InvalidOperationException ex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(ex.Message);
        }
    });

    app.MapPost("/admin/apply", async (HttpContext context, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var hasScope = TryResolveAdminScope(context, scopeOptions, out var scope, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(error);
            return;
        }

        try
        {
            var accepted = await AdminApplyJobRuntime.StartApplyAsync(
                hasScope,
                hasScope ? scope : null,
                scopeOptions,
                adminOptions,
                logger,
                context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status202Accepted;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(accepted, adminApiJsonOptions));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Admin apply preflight rejected.");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            await context.Response.WriteAsync("Apply preflight was canceled.");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Admin apply preflight timed out.");
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsync(
                $"Apply preflight timed out after {AdminApplyJobRuntime.PreflightTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin apply preflight failed.");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Failed to start sandbox admin apply.");
        }
    });

    app.MapGet("/admin/apply/jobs/{jobId}", async (HttpContext context, string jobId, ILogger<Program> logger) =>
    {
        if (!AuthorizeAdminRequest(context, adminOptions, logger))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        if (string.IsNullOrWhiteSpace(jobId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("jobId is required.");
            return;
        }

        if (!AdminApplyJobRuntime.TryGetStatus(jobId, adminOptions, out var status) || status is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Apply job was not found.");
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(status, adminApiJsonOptions));
    });

}

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

static bool AuthorizeAdminRequest(HttpContext context, AdminApiOptions options, ILogger logger)
{
    var suppliedToken = context.Request.Headers["X-Script-Agent-Admin-Token"].ToString();
    if (string.IsNullOrEmpty(suppliedToken) || string.IsNullOrWhiteSpace(options.AdminToken))
    {
        logger.LogWarning("SECURITY: admin token missing. path={Path}", LogValueSanitizer.Sanitize(context.Request.Path.Value));
        return false;
    }

    if (!string.Equals(suppliedToken, options.AdminToken, StringComparison.Ordinal))
    {
        logger.LogWarning("SECURITY: admin token mismatch. path={Path}", LogValueSanitizer.Sanitize(context.Request.Path.Value));
        return false;
    }

    return true;
}

static async Task<string> ReadRequestBodyAsync(HttpContext context)
{
    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
    return await reader.ReadToEndAsync(context.RequestAborted);
}

static bool TryResolveAdminScope(
    HttpContext context,
    ScriptExecutionScopeOptions scopeOptions,
    out ScriptExecutionScope scope,
    out string error)
{
    scope = default!;
    error = string.Empty;

    var projectIdValue = context.Request.Query["projectId"].ToString();
    var guideIdValue = context.Request.Query["guideId"].ToString();
    if (string.IsNullOrWhiteSpace(projectIdValue) && string.IsNullOrWhiteSpace(guideIdValue))
    {
        return false;
    }

    if (!Guid.TryParse(projectIdValue, out var projectId) || projectId == Guid.Empty)
    {
        error = "projectId query parameter must be a non-empty GUID when scope is provided.";
        return false;
    }

    if (!Guid.TryParse(guideIdValue, out var guideId) || guideId == Guid.Empty)
    {
        error = "guideId query parameter must be a non-empty GUID when scope is provided.";
        return false;
    }

    scope = ScriptExecutionScopeRuntime.ResolveScope(projectId, guideId, scopeOptions);
    ScriptExecutionScopeRuntime.EnsureScopeDirectory(scope);
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

    if (!string.IsNullOrWhiteSpace(request.GuideId)
        && (!Guid.TryParse(request.GuideId, out var guideId) || guideId == Guid.Empty))
    {
        return ValidationResult.Failure("GuideId must be a non-empty GUID when provided");
    }

    var environmentValidation = ValidateExecutionEnvironment(request.Environment);
    if (!environmentValidation.IsValid)
    {
        return environmentValidation;
    }

    return ValidationResult.Success();
}

static Guid ResolveGuideScopeId(ScriptExecutionRequest request)
{
    if (Guid.TryParse(request.GuideId, out var guideId) && guideId != Guid.Empty)
    {
        return guideId;
    }

    return Guid.Parse(request.NotebookId);
}

static ValidationResult ValidateExecutionEnvironment(IReadOnlyDictionary<string, string>? environment)
{
    if (environment is null)
    {
        return ValidationResult.Success();
    }

    if (environment.Count > 128)
    {
        return ValidationResult.Failure("Environment contains too many entries");
    }

    foreach (var (key, value) in environment)
    {
        var keyValidation = ScriptExecutionScopeRuntime.ValidateEnvironmentKey(key);
        if (!keyValidation.IsValid)
        {
            return keyValidation;
        }

        if (value.Length > 64 * 1024)
        {
            return ValidationResult.Failure($"Environment value for '{key}' exceeds maximum size");
        }
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
    NotebookExecutionIdentity? executionIdentity,
    ScriptExecutionScopeOptions scopeOptions,
    AdminApiOptions adminOptions)
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

        var scope = ScriptExecutionScopeRuntime.ResolveScope(
            Guid.Parse(request.ProjectId),
            ResolveGuideScopeId(request),
            scopeOptions);
        ScriptExecutionScopeRuntime.EnsureScopeDirectory(scope);
        if (request.ScriptType == ScriptType.Python)
        {
            try
            {
                await ScriptExecutionScopeRuntime.EnsurePythonVenvAsync(scope, scopeOptions, logger, CancellationToken.None);
                await ScriptExecutionScopeRuntime.EnsureScopeRequirementsForExecutionAsync(scope, scopeOptions, adminOptions, logger, CancellationToken.None);
            }
            catch (Exception ex) when (!scopeOptions.RequireScopedPythonVenv)
            {
                logger.LogWarning(
                    ex,
                    "Scoped Python venv provisioning failed for project={ProjectId} guide={GuideId}; falling back to system python because SCRIPT_EXECUTION_REQUIRE_SCOPED_VENV=false.",
                    scope.ProjectId,
                    scope.GuideScopeId);
            }
        }

        var scopedEnvironment = ScriptExecutionScopeRuntime.BuildScriptEnvironment(scope, request.Environment, request.WorkingDirectory, logger);
        var (commandFile, commandArgs) = GetScriptCommand(request.ScriptType, scriptFilePath, scope);
        (commandFile, commandArgs) = ApplyPrivacyWrapper(commandFile, commandArgs);
        using var cts = new CancellationTokenSource(config.MaxExecutionTime);
        ScriptProcessResult run;
        if (executionIdentity is not null && OperatingSystem.IsLinux())
        {
            run = await ExecuteScriptWithSetprivAsync(commandFile, commandArgs, scopedEnvironment, request.WorkingDirectory, executionIdentity, cts.Token);
        }
        else
        {
            run = await RunScriptProcessAsync(commandFile, commandArgs, scopedEnvironment, request.WorkingDirectory, cts.Token);
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

static async Task<ScriptProcessResult> ExecuteScriptWithSetprivAsync(
    string commandFile,
    string[] commandArgs,
    IReadOnlyDictionary<string, string?> environmentVariables,
    string workingDirectory,
    NotebookExecutionIdentity executionIdentity,
    CancellationToken cancellationToken)
{
    var setprivArgs = new List<string>
    {
        "--reuid",
        executionIdentity.Uid.ToString(),
        "--regid",
        executionIdentity.Gid.ToString(),
        "--init-groups",
        "--no-new-privs",
        "--bounding-set",
        "-all",
        "--",
        commandFile
    };
    setprivArgs.AddRange(commandArgs);
    return await RunScriptProcessAsync("setpriv", setprivArgs.ToArray(), environmentVariables, workingDirectory, cancellationToken);
}

static async Task<ScriptProcessResult> RunScriptProcessAsync(
    string commandFile,
    string[] commandArgs,
    IReadOnlyDictionary<string, string?> environmentVariables,
    string workingDirectory,
    CancellationToken cancellationToken)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = commandFile,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    foreach (var commandArg in commandArgs)
    {
        startInfo.ArgumentList.Add(commandArg);
    }

    startInfo.Environment.Clear();
    foreach (var pair in environmentVariables)
    {
        if (pair.Value is not null)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    using var process = System.Diagnostics.Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start process '{commandFile}'.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    return new ScriptProcessResult(
        process.ExitCode,
        await stdoutTask,
        await stderrTask);
}

static (string FileName, string[] Arguments) ApplyPrivacyWrapper(string commandFile, string[] commandArgs)
{
    if (!OperatingSystem.IsLinux())
    {
        return (commandFile, commandArgs);
    }

    var wrapper = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_PRIVACY_WRAPPER");
    if (string.IsNullOrWhiteSpace(wrapper))
    {
        wrapper = "/usr/local/bin/ga-script-exec";
    }

    if (!File.Exists(wrapper))
    {
        return (commandFile, commandArgs);
    }

    var wrappedArgs = new List<string> { commandFile };
    wrappedArgs.AddRange(commandArgs);
    return (wrapper, wrappedArgs.ToArray());
}

static (string FileName, string[] Arguments) GetScriptCommand(
    ScriptType scriptType,
    string scriptFilePath,
    ScriptExecutionScope scope) => scriptType switch
{
    ScriptType.Bash => ("bash", new[] { scriptFilePath }),
    ScriptType.PowerShell => ("pwsh", new[] { "-File", scriptFilePath }),
    ScriptType.Python => (
        File.Exists(scope.PythonExecutablePath) ? scope.PythonExecutablePath : "python",
        new[] { scriptFilePath }),
    _ => throw new ArgumentOutOfRangeException(nameof(scriptType), scriptType, null)
};

internal sealed record ScriptExecutionScopeOptions(
    string StateRootPath,
    string? PythonVenvRelativePath,
    string? PythonBootstrapCommand,
    bool RequireScopedPythonVenv,
    string? BasePythonVenvPath);

internal sealed record AdminApiOptions(
    bool Enabled,
    string? AdminToken,
    string StateDirectoryPath,
    bool FailOpen);

internal sealed record AdminApplyResult(
    string Status,
    int ScopesApplied,
    int ScopesSkipped,
    string[] Errors,
    AdminApplyResultDetails? Apt = null,
    AdminInstallScriptsApplyDetails? InstallScripts = null);

internal sealed record AdminApplyResultDetails(
    string Status,
    int Applied,
    int Skipped,
    string[] Errors);

internal sealed record ScriptProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal sealed record ScriptExecutionScope(
    Guid ProjectId,
    Guid GuideScopeId,
    string ScopeRootPath,
    string PythonVenvPath,
    string RequirementsFilePath,
    string AppliedStateFilePath)
{
    public string PythonExecutablePath =>
        OperatingSystem.IsWindows()
            ? Path.Combine(PythonVenvPath, "Scripts", "python.exe")
            : Path.Combine(PythonVenvPath, "bin", "python");
}

internal static class ScriptExecutionScopeRuntime
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> VenvLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex EnvironmentVariableNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RequirementsPackageNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NormalizedPythonPackageSeparatorPattern = new("[-_.]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ProtectedTopLevelPythonPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "pip",
        "setuptools",
        "wheel"
    };
    private static readonly HashSet<string> ReservedEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCRIPT_EXECUTION_SCOPE_ROOT",
        "SCRIPT_EXECUTION_SCOPE_PROJECT_ID",
        "SCRIPT_EXECUTION_SCOPE_GUIDE_ID",
        "SCRIPT_EXECUTION_SCOPE_CREDENTIALS_FILE",
        "PATH",
        "HOME",
        "USER",
        "USERNAME",
        "SHELL",
        "LD_PRELOAD",
        "LD_LIBRARY_PATH",
        "DYLD_INSERT_LIBRARIES",
        "PYTHONPATH",
        "PYTHONHOME",
        "BASH_ENV",
        "ENV",
        "PROMPT_COMMAND",
        "PSModulePath",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "ASPNETCORE_ENVIRONMENT",
        "DOTNET_ENVIRONMENT",
        "SCRIPT_EXECUTION_AGENT_TOKEN",
        "SCRIPT_EXECUTION_ADMIN_TOKEN"
    };

    private static readonly string[] AllowedInheritedEnvironmentKeys =
    {
        "CUDA_VISIBLE_DEVICES",
        "NVIDIA_VISIBLE_DEVICES",
        "NVIDIA_DRIVER_CAPABILITIES",
        "ROCR_VISIBLE_DEVICES",
        "HIP_VISIBLE_DEVICES",
        "HSA_OVERRIDE_GFX_VERSION",
        "HF_HOME",
        "TRANSFORMERS_CACHE",
        "TORCH_HOME",
        "PLAYWRIGHT_BROWSERS_PATH",
        "SSL_CERT_FILE",
        "REQUESTS_CA_BUNDLE",
        "LD_LIBRARY_PATH",
        "CPLUS_INCLUDE_PATH",
        "C_INCLUDE_PATH"
    };

    public static ScriptExecutionScope ResolveScope(
        Guid projectId,
        Guid guideScopeId,
        ScriptExecutionScopeOptions options)
    {
        var venvRelativePath = NormalizeRelativePath(options.PythonVenvRelativePath, "python-venv");

        var scopeRoot = Path.Combine(
            options.StateRootPath,
            $"project-{projectId:N}",
            $"guide-{guideScopeId:N}");

        return new ScriptExecutionScope(
            projectId,
            guideScopeId,
            scopeRoot,
            Path.Combine(scopeRoot, venvRelativePath),
            Path.Combine(scopeRoot, "requirements.txt"),
            Path.Combine(scopeRoot, "applied-state.json"));
    }

    public static void EnsureScopeDirectory(ScriptExecutionScope scope)
    {
        Directory.CreateDirectory(scope.ScopeRootPath);
        foreach (var filePath in new[] { scope.RequirementsFilePath, scope.AppliedStateFilePath, AdminInstallScriptsRuntime.GetInstallScriptsPath(scope) })
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    public static async Task EnsurePythonVenvAsync(
        ScriptExecutionScope scope,
        ScriptExecutionScopeOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (File.Exists(scope.PythonExecutablePath))
        {
            EnsureBasePythonRuntimeExtension(scope, options, logger);
            return;
        }

        var venvLock = VenvLocks.GetOrAdd(scope.PythonVenvPath, static _ => new SemaphoreSlim(1, 1));
        await venvLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(scope.PythonExecutablePath))
            {
                EnsureBasePythonRuntimeExtension(scope, options, logger);
                return;
            }

            var venvParent = Path.GetDirectoryName(scope.PythonVenvPath);
            if (!string.IsNullOrWhiteSpace(venvParent))
            {
                Directory.CreateDirectory(venvParent);
            }

            var attempts = GetPythonBootstrapCommands(options.PythonBootstrapCommand);
            var failures = new List<string>();
            foreach (var command in attempts)
            {
                try
                {
                    var (created, createDetail) = await TryCreatePythonVenvAsync(
                        command,
                        scope.PythonVenvPath,
                        withoutPip: false,
                        cancellationToken);
                    if (created && File.Exists(scope.PythonExecutablePath))
                    {
                        EnsureBasePythonRuntimeExtension(scope, options, logger);
                        logger.LogInformation(
                            "Created scoped Python virtual environment for project={ProjectId} guide={GuideId} using command={Command}.",
                            scope.ProjectId,
                            scope.GuideScopeId,
                            LogValueSanitizer.Sanitize(command));
                        return;
                    }

                    TryDeletePythonVenvDirectory(scope.PythonVenvPath);
                    (created, createDetail) = await TryCreatePythonVenvAsync(
                        command,
                        scope.PythonVenvPath,
                        withoutPip: true,
                        cancellationToken);
                    var pipReady = false;
                    var pipDetail = created ? string.Empty : "python executable missing";
                    if (created)
                    {
                        for (var pipAttempt = 0; pipAttempt < 3; pipAttempt++)
                        {
                            (pipReady, pipDetail) = await TryBootstrapScopedVenvPipAsync(
                                scope.PythonExecutablePath,
                                cancellationToken);
                            if (pipReady)
                            {
                                break;
                            }

                            await Task.Delay(200, cancellationToken);
                        }
                    }
                    if (created && pipReady && File.Exists(scope.PythonExecutablePath))
                    {
                        EnsureBasePythonRuntimeExtension(scope, options, logger);
                        logger.LogInformation(
                            "Created scoped Python virtual environment for project={ProjectId} guide={GuideId} using command={Command} with separate ensurepip bootstrap.",
                            scope.ProjectId,
                            scope.GuideScopeId,
                            LogValueSanitizer.Sanitize(command));
                        return;
                    }

                    TryDeletePythonVenvDirectory(scope.PythonVenvPath);
                    failures.Add(
                        $"{command}: venv={(created ? "ok" : createDetail)} pip={(pipReady ? "ok" : pipDetail)}");
                }
                catch (Exception ex)
                {
                    TryDeletePythonVenvDirectory(scope.PythonVenvPath);
                    failures.Add($"{command}: {LogValueSanitizer.Sanitize(ex.Message)}");
                }
            }

            throw new InvalidOperationException(
                $"Failed to create scoped Python virtual environment at '{scope.PythonVenvPath}'. Attempts: {string.Join("; ", failures)}");
        }
        finally
        {
            venvLock.Release();
        }
    }

    private static async Task<(bool Success, string Detail)> TryCreatePythonVenvAsync(
        string command,
        string venvPath,
        bool withoutPip,
        CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(args =>
            {
                args.Add("-m").Add("venv");
                if (withoutPip)
                {
                    args.Add("--without-pip");
                }

                args.Add(venvPath);
            })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode == 0)
        {
            return (true, string.Empty);
        }

        var detail = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : !string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardOutput.Trim()
                : $"exit {result.ExitCode}";
        return (false, detail);
    }

    private static async Task<(bool Success, string Detail)> TryBootstrapScopedVenvPipAsync(
        string pythonExecutablePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(pythonExecutablePath))
        {
            return (false, "python executable missing");
        }

        var result = await Cli.Wrap(pythonExecutablePath)
            .WithArguments(args => args
                .Add("-m")
                .Add("ensurepip")
                .Add("--upgrade")
                .Add("--default-pip"))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (result.ExitCode == 0)
        {
            return (true, string.Empty);
        }

        var detail = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : $"exit {result.ExitCode}";
        return (false, detail);
    }

    private static void TryDeletePythonVenvDirectory(string venvPath)
    {
        if (!Directory.Exists(venvPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(venvPath, recursive: true);
                if (!Directory.Exists(venvPath))
                {
                    return;
                }
            }
            catch
            {
                // Best-effort cleanup before retrying venv creation.
            }

            Thread.Sleep(100);
        }
    }

    private static void EnsureBasePythonRuntimeExtension(
        ScriptExecutionScope scope,
        ScriptExecutionScopeOptions options,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.BasePythonVenvPath)
            || !Directory.Exists(options.BasePythonVenvPath)
            || string.Equals(
                Path.GetFullPath(options.BasePythonVenvPath),
                Path.GetFullPath(scope.PythonVenvPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        var scopedSitePackages = FindPythonSitePackages(scope.PythonVenvPath).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scopedSitePackages))
        {
            return;
        }

        var baseSitePackages = FindPythonSitePackages(options.BasePythonVenvPath)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(scopedSitePackages),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .ToArray();
        if (baseSitePackages.Length == 0)
        {
            return;
        }

        var pthPath = Path.Combine(scopedSitePackages, "guideants-base-runtime.pth");
        var content = string.Join(Environment.NewLine, baseSitePackages) + Environment.NewLine;
        if (File.Exists(pthPath) && string.Equals(File.ReadAllText(pthPath), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(pthPath, content);
        logger.LogInformation(
            "Extended scoped Python venv for project={ProjectId} guide={GuideId} with base runtime packages from {BasePythonVenvPath}.",
            scope.ProjectId,
            scope.GuideScopeId,
            LogValueSanitizer.Sanitize(options.BasePythonVenvPath));
    }

    public static async Task EnsureScopeRequirementsForExecutionAsync(
        ScriptExecutionScope scope,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        EnsureScopeDirectory(scope);
        await EnsurePythonVenvAsync(scope, scopeOptions, logger, cancellationToken);

        var requirementsPath = File.Exists(scope.RequirementsFilePath)
            ? scope.RequirementsFilePath
            : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
        var requirementsText = File.Exists(requirementsPath)
            ? await File.ReadAllTextAsync(requirementsPath, cancellationToken)
            : string.Empty;
        var validation = ValidateRequirements(requirementsText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredTopLevelPackages = ParseTopLevelRequirementPackageNames(requirementsText);
        var requirementsHash = ComputeSha256(requirementsText);
        var appliedState = AdminScopeAppliedStateRuntime.Read(scope);
        if (requirementsHash == appliedState.RequirementsHash)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(requirementsText))
        {
            var result = await Cli.Wrap(scope.PythonExecutablePath)
                .WithArguments(args => args
                    .Add("-m")
                    .Add("pip")
                    .Add("--disable-pip-version-check")
                    .Add("install")
                    .Add("-r")
                    .Add(requirementsPath))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(FormatPipFailure(result));
            }
        }

        await PruneUnmanagedTopLevelPackagesAsync(
            scope.PythonExecutablePath,
            desiredTopLevelPackages,
            cancellationToken);

        await AdminScopeAppliedStateRuntime.WriteAsync(
            scope,
            requirementsHash,
            requirementsPath,
            desiredTopLevelPackages,
            appliedState.InstallScriptsHash,
            appliedState.InstallScriptStepResults,
            cancellationToken);
        logger.LogInformation(
            "Synced scoped Python requirements for execution on project={ProjectId} guide={GuideId}.",
            scope.ProjectId,
            scope.GuideScopeId);
    }

    public static async Task<AdminApplyResult> ApplyScopeRequirementsAsync(
        ScriptExecutionScope scope,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        EnsureScopeDirectory(scope);
        await EnsurePythonVenvAsync(scope, scopeOptions, logger, cancellationToken);

        var requirementsPath = File.Exists(scope.RequirementsFilePath)
            ? scope.RequirementsFilePath
            : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
        var requirementsText = File.Exists(requirementsPath)
            ? await File.ReadAllTextAsync(requirementsPath, cancellationToken)
            : string.Empty;
        var validation = ValidateRequirements(requirementsText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredTopLevelPackages = ParseTopLevelRequirementPackageNames(requirementsText);
        var requirementsHash = ComputeSha256(requirementsText);
        var appliedState = AdminScopeAppliedStateRuntime.Read(scope);
        var unmanagedPackagesBeforeInstall = await GetUnmanagedTopLevelPackagesAsync(
            scope.PythonExecutablePath,
            desiredTopLevelPackages,
            cancellationToken);
        var installScriptsDocument = AdminInstallScriptsRuntime.ReadDocument(scope);
        var installScriptsHash = AdminInstallScriptsRuntime.ComputeDocumentHash(installScriptsDocument);
        var requirementsNeedsApply = requirementsHash != appliedState.RequirementsHash || unmanagedPackagesBeforeInstall.Count > 0;
        var scriptsNeedApply = AdminInstallScriptsRuntime.NeedsApply(
            installScriptsHash,
            appliedState.InstallScriptsHash,
            installScriptsDocument.Scripts.Count);
        if (!requirementsNeedsApply && !scriptsNeedApply)
        {
            return new AdminApplyResult("skipped", 0, 1, Array.Empty<string>());
        }

        if (requirementsNeedsApply && requirementsHash != appliedState.RequirementsHash && !string.IsNullOrWhiteSpace(requirementsText))
        {
            var result = await Cli.Wrap(scope.PythonExecutablePath)
                .WithArguments(args => args
                    .Add("-m")
                    .Add("pip")
                    .Add("--disable-pip-version-check")
                    .Add("install")
                    .Add("-r")
                    .Add(requirementsPath))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(FormatPipFailure(result));
            }
        }

        if (requirementsNeedsApply)
        {
            await PruneUnmanagedTopLevelPackagesAsync(
                scope.PythonExecutablePath,
                desiredTopLevelPackages,
                cancellationToken);
        }

        AdminInstallScriptsApplyDetails? installScriptsDetails = null;
        IReadOnlyList<AdminInstallScriptStepResult> installScriptStepResults = appliedState.InstallScriptStepResults;
        if (scriptsNeedApply)
        {
            installScriptsDetails = await ApplyScopeInstallScriptsAsync(
                scope,
                installScriptsDocument,
                logger,
                cancellationToken);
            installScriptStepResults = installScriptsDetails.StepResults;
        }

        await AdminScopeAppliedStateRuntime.WriteAsync(
            scope,
            requirementsHash,
            requirementsPath,
            desiredTopLevelPackages,
            installScriptsHash,
            installScriptStepResults,
            cancellationToken);
        logger.LogInformation(
            "Applied scoped sandbox setup for project={ProjectId} guide={GuideId}. requirementsApplied={RequirementsApplied} installScriptsApplied={InstallScriptsApplied}",
            scope.ProjectId,
            scope.GuideScopeId,
            requirementsNeedsApply,
            scriptsNeedApply);
        return new AdminApplyResult("applied", 1, 0, Array.Empty<string>(), InstallScripts: installScriptsDetails);
    }

    public static async Task<AdminInstallScriptsApplyDetails> ApplyScopeInstallScriptsAsync(
        ScriptExecutionScope scope,
        AdminInstallScriptsDocument document,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (document.Scripts.Count == 0)
        {
            return new AdminInstallScriptsApplyDetails("skipped", 0, 0, 0, Array.Empty<AdminInstallScriptStepResult>());
        }

        var workDirectory = Path.Combine(scope.ScopeRootPath, "install-scripts-work");
        Directory.CreateDirectory(workDirectory);
        var environment = BuildScriptEnvironment(scope, null, workDirectory, logger);
        var stepResults = new List<AdminInstallScriptStepResult>();

        foreach (var step in document.Scripts.OrderBy(static script => script.Order))
        {
            if (!Enum.TryParse<ScriptType>(step.ScriptType, ignoreCase: true, out var scriptType)
                || scriptType is not (ScriptType.Python or ScriptType.Bash))
            {
                throw new InvalidOperationException($"install script '{step.Id}' has invalid scriptType.");
            }

            var extension = scriptType == ScriptType.Python ? ".py" : ".sh";
            var scriptPath = Path.Combine(workDirectory, $"{step.Order:000}-{step.Id}{extension}");
            await File.WriteAllTextAsync(scriptPath, step.Script, cancellationToken);

            var commandFile = scriptType == ScriptType.Python
                ? (File.Exists(scope.PythonExecutablePath) ? scope.PythonExecutablePath : "python3")
                : "bash";
            var commandArgs = new[] { scriptPath };
            (commandFile, commandArgs) = ApplyInstallScriptPrivacyWrapper(commandFile, commandArgs);
            var run = await ExecuteInstallScriptProcessAsync(commandFile, commandArgs, environment, workDirectory, cancellationToken);
            if (run.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(run.StandardError) ? run.StandardOutput : run.StandardError;
                var failedResult = new AdminInstallScriptStepResult(
                    step.Id,
                    step.Order,
                    step.Name,
                    "failed",
                    run.ExitCode,
                    error.Trim(),
                    DateTimeOffset.UtcNow);
                stepResults.Add(failedResult);
                throw new InvalidOperationException(
                    $"install script '{step.Id}' failed with exit code {run.ExitCode}: {failedResult.Error}");
            }

            stepResults.Add(new AdminInstallScriptStepResult(
                step.Id,
                step.Order,
                step.Name,
                "succeeded",
                0,
                null,
                DateTimeOffset.UtcNow));
            logger.LogInformation(
                "Applied install script for project={ProjectId} guide={GuideId} scriptId={ScriptId} order={Order}",
                scope.ProjectId,
                scope.GuideScopeId,
                step.Id,
                step.Order);
        }

        return new AdminInstallScriptsApplyDetails("applied", stepResults.Count, 0, 0, stepResults);
    }

    private static (string FileName, string[] Arguments) ApplyInstallScriptPrivacyWrapper(string commandFile, string[] commandArgs)
    {
        if (!OperatingSystem.IsLinux())
        {
            return (commandFile, commandArgs);
        }

        var wrapper = Environment.GetEnvironmentVariable("SCRIPT_EXECUTION_PRIVACY_WRAPPER");
        if (string.IsNullOrWhiteSpace(wrapper))
        {
            wrapper = "/usr/local/bin/ga-script-exec";
        }

        if (!File.Exists(wrapper))
        {
            return (commandFile, commandArgs);
        }

        var wrappedArgs = new List<string> { commandFile };
        wrappedArgs.AddRange(commandArgs);
        return (wrapper, wrappedArgs.ToArray());
    }

    private static async Task<ScriptProcessResult> ExecuteInstallScriptProcessAsync(
        string commandFile,
        string[] commandArgs,
        IReadOnlyDictionary<string, string?> environmentVariables,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = commandFile,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var commandArg in commandArgs)
        {
            startInfo.ArgumentList.Add(commandArg);
        }

        startInfo.Environment.Clear();
        foreach (var pair in environmentVariables)
        {
            if (pair.Value is not null)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{commandFile}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ScriptProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    public static async Task PreflightScopeRequirementsAsync(
        ScriptExecutionScope scope,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        EnsureScopeDirectory(scope);
        await EnsurePythonVenvAsync(scope, scopeOptions, logger, cancellationToken);

        var requirementsPath = File.Exists(scope.RequirementsFilePath)
            ? scope.RequirementsFilePath
            : AdminStateRuntime.GetGlobalRequirementsPath(adminOptions);
        var requirementsText = File.Exists(requirementsPath)
            ? await File.ReadAllTextAsync(requirementsPath, cancellationToken)
            : string.Empty;
        var validation = ValidateRequirements(requirementsText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredTopLevelPackages = ParseTopLevelRequirementPackageNames(requirementsText);
        var requirementsHash = ComputeSha256(requirementsText);
        var appliedState = AdminScopeAppliedStateRuntime.Read(scope);
        var unmanagedPackagesBeforeInstall = await GetUnmanagedTopLevelPackagesAsync(
            scope.PythonExecutablePath,
            desiredTopLevelPackages,
            cancellationToken);
        if (requirementsHash != appliedState.RequirementsHash || unmanagedPackagesBeforeInstall.Count > 0)
        {
            if (requirementsHash != appliedState.RequirementsHash && !string.IsNullOrWhiteSpace(requirementsText))
            {
                var result = await Cli.Wrap(scope.PythonExecutablePath)
                    .WithArguments(args => args
                        .Add("-m")
                        .Add("pip")
                        .Add("--disable-pip-version-check")
                        .Add("install")
                        .Add("--dry-run")
                        .Add("-r")
                        .Add(requirementsPath))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(FormatPipFailure(result));
                }
            }
        }

        var installScriptsDocument = AdminInstallScriptsRuntime.ReadDocument(scope);
        await AdminInstallScriptsRuntime.PreflightSyntaxAsync(
            scope,
            scopeOptions,
            installScriptsDocument,
            logger,
            cancellationToken);
    }

    private static string FormatPipFailure(BufferedCommandResult result)
    {
        var stderr = result.StandardError?.Trim();
        var stdout = result.StandardOutput?.Trim();
        var detail = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
        if (string.IsNullOrWhiteSpace(detail))
        {
            return $"pip install failed with exit code {result.ExitCode}.";
        }

        return $"pip install failed with exit code {result.ExitCode}: {detail}";
    }

    public static IReadOnlyDictionary<string, string?> BuildScriptEnvironment(
        ScriptExecutionScope scope,
        IReadOnlyDictionary<string, string>? requestEnvironment,
        string workingDirectory,
        ILogger logger)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = BuildDefaultPath(scope),
            ["HOME"] = workingDirectory,
            ["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8",
            ["LC_ALL"] = Environment.GetEnvironmentVariable("LC_ALL") ?? "C.UTF-8",
            ["GUIDEANTS_PROJECT_ID"] = scope.ProjectId.ToString("D"),
            ["GUIDEANTS_GUIDE_ID"] = scope.GuideScopeId.ToString("D")
        };

        if (Directory.Exists(scope.PythonVenvPath))
        {
            environment["VIRTUAL_ENV"] = scope.PythonVenvPath;
        }

        foreach (var inherited in GetAllowedInheritedEnvironment())
        {
            if (!environment.ContainsKey(inherited.Key))
            {
                environment[inherited.Key] = inherited.Value;
            }
        }

        if (requestEnvironment is not null)
        {
            foreach (var pair in requestEnvironment)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        logger.LogInformation(
            "Built script environment for project={ProjectId} guide={GuideId}. injectedEntries={InjectedCount}",
            scope.ProjectId,
            scope.GuideScopeId,
            requestEnvironment?.Count ?? 0);
        return environment;
    }

    public static ValidationResult ValidateEnvironmentKey(string key)
    {
        if (!EnvironmentVariableNamePattern.IsMatch(key))
        {
            return ValidationResult.Failure("Environment variable name must be valid.");
        }

        if (ReservedEnvironmentKeys.Contains(key)
            || key.StartsWith("SCRIPT_EXECUTION_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("GUIDEANTS_", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure("Environment variable name is reserved by ScriptExecutionAgent.");
        }

        return ValidationResult.Success();
    }

    public static ValidationResult ValidateRequirements(string requirementsText)
    {
        var lineNumber = 0;
        foreach (var rawLine in requirementsText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("-", StringComparison.OrdinalIgnoreCase)
                || line.Contains("://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("git+", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith(".", StringComparison.Ordinal)
                || line.StartsWith("/", StringComparison.Ordinal))
            {
                return ValidationResult.Failure($"requirements.txt line {lineNumber} uses a blocked install source or option.");
            }
        }

        return ValidationResult.Success();
    }

    public static IEnumerable<ScriptExecutionScope> EnumerateExistingScopes(ScriptExecutionScopeOptions options)
    {
        if (!Directory.Exists(options.StateRootPath))
        {
            yield break;
        }

        foreach (var projectDirectory in Directory.EnumerateDirectories(options.StateRootPath, "project-*"))
        {
            var projectName = Path.GetFileName(projectDirectory);
            if (!Guid.TryParse(projectName["project-".Length..], out var projectId))
            {
                continue;
            }

            foreach (var guideDirectory in Directory.EnumerateDirectories(projectDirectory, "guide-*"))
            {
                var guideName = Path.GetFileName(guideDirectory);
                if (!Guid.TryParse(guideName["guide-".Length..], out var guideId))
                {
                    continue;
                }

                yield return ResolveScope(projectId, guideId, options);
            }
        }
    }

    private static string BuildDefaultPath(ScriptExecutionScope scope)
    {
        var segments = new List<string>();
        if (Directory.Exists(Path.Combine(scope.PythonVenvPath, "bin")))
        {
            segments.Add(Path.Combine(scope.PythonVenvPath, "bin"));
        }

        segments.AddRange(new[]
        {
            "/opt/venv/bin",
            "/usr/local/sbin",
            "/usr/local/bin",
            "/usr/sbin",
            "/usr/bin",
            "/sbin",
            "/bin"
        });

        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        }

        return string.Join(":", segments.Distinct(StringComparer.Ordinal));
    }

    private static IEnumerable<KeyValuePair<string, string>> GetAllowedInheritedEnvironment()
    {
        foreach (var key in AllowedInheritedEnvironmentKeys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IReadOnlySet<string> ParseTopLevelRequirementPackageNames(string requirementsText)
    {
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in requirementsText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = RequirementsPackageNamePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            packageNames.Add(NormalizePythonPackageName(match.Value));
        }

        return packageNames;
    }

    private static async Task<IReadOnlyList<string>> GetUnmanagedTopLevelPackagesAsync(
        string pythonExecutablePath,
        IReadOnlySet<string> desiredTopLevelPackages,
        CancellationToken cancellationToken)
    {
        var installedTopLevelPackages = await GetInstalledTopLevelPackagesAsync(pythonExecutablePath, cancellationToken);
        return installedTopLevelPackages
            .Where(packageName =>
                !ProtectedTopLevelPythonPackages.Contains(packageName)
                && !desiredTopLevelPackages.Contains(packageName))
            .OrderBy(static packageName => packageName, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> PruneUnmanagedTopLevelPackagesAsync(
        string pythonExecutablePath,
        IReadOnlySet<string> desiredTopLevelPackages,
        CancellationToken cancellationToken)
    {
        var removedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var toRemove = await GetUnmanagedTopLevelPackagesAsync(
                pythonExecutablePath,
                desiredTopLevelPackages,
                cancellationToken);
            if (toRemove.Count == 0)
            {
                break;
            }

            var uninstall = await Cli.Wrap(pythonExecutablePath)
                .WithArguments(args =>
                {
                    args.Add("-m")
                        .Add("pip")
                        .Add("--disable-pip-version-check")
                        .Add("uninstall")
                        .Add("-y");
                    foreach (var packageName in toRemove)
                    {
                        args.Add(packageName);
                    }
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            if (uninstall.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"pip uninstall failed with exit code {uninstall.ExitCode} for packages [{string.Join(", ", toRemove)}]: {uninstall.StandardError}");
            }

            foreach (var packageName in toRemove)
            {
                removedPackages.Add(packageName);
            }
        }

        return removedPackages
            .OrderBy(static packageName => packageName, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlySet<string>> GetInstalledTopLevelPackagesAsync(
        string pythonExecutablePath,
        CancellationToken cancellationToken)
    {
        var listResult = await Cli.Wrap(pythonExecutablePath)
            .WithArguments(args => args
                .Add("-m")
                .Add("pip")
                .Add("--disable-pip-version-check")
                .Add("list")
                .Add("--not-required")
                .Add("--format=json"))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if (listResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pip list --not-required failed with exit code {listResult.ExitCode}: {listResult.StandardError}");
        }

        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = listResult.StandardOutput;
        if (string.IsNullOrWhiteSpace(output))
        {
            return packages;
        }

        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return packages;
        }

        foreach (var package in document.RootElement.EnumerateArray())
        {
            if (!package.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            packages.Add(NormalizePythonPackageName(name));
        }

        return packages;
    }

    private static string NormalizePythonPackageName(string packageName) =>
        NormalizedPythonPackageSeparatorPattern.Replace(packageName.Trim().ToLowerInvariant(), "-");

    private static string NormalizeRelativePath(string? candidate, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();
        if (Path.IsPathRooted(value))
        {
            throw new InvalidOperationException($"Scoped path '{value}' must be relative.");
        }

        var normalized = value.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return fallback;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Scoped path '{value}' cannot traverse parent directories.");
        }

        return Path.Combine(segments);
    }

    private static IReadOnlyList<string> FindPythonSitePackages(string venvPath)
    {
        if (string.IsNullOrWhiteSpace(venvPath) || !Directory.Exists(venvPath))
        {
            return Array.Empty<string>();
        }

        if (OperatingSystem.IsWindows())
        {
            var sitePackages = Path.Combine(venvPath, "Lib", "site-packages");
            return Directory.Exists(sitePackages)
                ? new[] { sitePackages }
                : Array.Empty<string>();
        }

        var candidates = new List<string>();
        foreach (var libDirectoryName in new[] { "lib", "lib64" })
        {
            var libDirectory = Path.Combine(venvPath, libDirectoryName);
            if (!Directory.Exists(libDirectory))
            {
                continue;
            }

            candidates.AddRange(
                Directory.EnumerateDirectories(libDirectory, "python*")
                    .Select(pythonDirectory => Path.Combine(pythonDirectory, "site-packages"))
                    .Where(Directory.Exists));
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> GetPythonBootstrapCommands(string? configuredBootstrapCommand)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredBootstrapCommand))
        {
            commands.Add(configuredBootstrapCommand.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            commands.Add("python");
        }
        else
        {
            commands.Add("python3");
            commands.Add("python");
        }

        return commands
            .Where(static cmd => !string.IsNullOrWhiteSpace(cmd))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal static class AdminStateRuntime
{
    public static async Task InitializeAsync(
        AdminApiOptions adminOptions,
        ScriptExecutionScopeOptions scopeOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(adminOptions.StateDirectoryPath);

            var requirementsPath = GetGlobalRequirementsPath(adminOptions);
            if (!File.Exists(requirementsPath))
            {
                await AtomicFile.WriteAllTextAsync(requirementsPath, string.Empty, cancellationToken);
            }

            var aptPackagesPath = GetAptPackagesPath(adminOptions);
            if (!File.Exists(aptPackagesPath))
            {
                await AtomicFile.WriteAllTextAsync(aptPackagesPath, string.Empty, cancellationToken);
            }

            var requirementsValidation = ScriptExecutionScopeRuntime.ValidateRequirements(
                await File.ReadAllTextAsync(requirementsPath, cancellationToken));
            if (!requirementsValidation.IsValid)
            {
                throw new InvalidOperationException(requirementsValidation.ErrorMessage);
            }

            var aptPackagesValidation = ValidateAptPackages(await File.ReadAllTextAsync(aptPackagesPath, cancellationToken));
            if (!aptPackagesValidation.IsValid)
            {
                throw new InvalidOperationException(aptPackagesValidation.ErrorMessage);
            }

            var aptResult = await ApplyGlobalAptPackagesAsync(adminOptions, logger, cancellationToken);
            var result = await ApplyAllKnownScopesAsync(scopeOptions, adminOptions, logger, cancellationToken);
            logger.LogInformation(
                "ScriptExecutionAgent admin state initialized. aptStatus={AptStatus} status={Status} scopesApplied={Applied} scopesSkipped={Skipped}",
                aptResult.Status,
                result.Status,
                result.ScopesApplied,
                result.ScopesSkipped);
        }
        catch (Exception ex)
        {
            if (!adminOptions.FailOpen)
            {
                throw;
            }

            logger.LogWarning(ex, "ScriptExecutionAgent admin startup reconcile failed; continuing because SCRIPT_EXECUTION_ADMIN_FAIL_OPEN=true.");
        }
    }

    public static string GetGlobalRequirementsPath(AdminApiOptions options) => Path.Combine(options.StateDirectoryPath, "requirements.txt");

    public static string GetAptPackagesPath(AdminApiOptions options) => Path.Combine(options.StateDirectoryPath, "apt-packages.txt");

    public static string GetGlobalAppliedStatePath(AdminApiOptions options) => Path.Combine(options.StateDirectoryPath, "applied-state.json");

    public static string GetRequirementsPath(AdminApiOptions options, ScriptExecutionScope? scope) =>
        scope is null ? GetGlobalRequirementsPath(options) : scope.RequirementsFilePath;

    public static ValidationResult ValidateAptPackages(string packageText)
    {
        var lineNumber = 0;
        foreach (var rawLine in packageText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var packageName = line.Split('#', 2)[0].Trim();
            if (!Regex.IsMatch(packageName, "^[a-z0-9][a-z0-9+.-]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            {
                return ValidationResult.Failure($"apt-packages.txt line {lineNumber} is not a valid package name.");
            }
        }

        return ValidationResult.Success();
    }

    public static async Task PreflightGlobalApplyAsync(
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await PreflightGlobalAptPackagesAsync(adminOptions, cancellationToken);

        foreach (var scope in ScriptExecutionScopeRuntime.EnumerateExistingScopes(scopeOptions))
        {
            await ScriptExecutionScopeRuntime.PreflightScopeRequirementsAsync(
                scope,
                scopeOptions,
                adminOptions,
                logger,
                cancellationToken);
        }
    }

    public static async Task PreflightGlobalAptPackagesAsync(
        AdminApiOptions adminOptions,
        CancellationToken cancellationToken)
    {
        var aptPackagesPath = GetAptPackagesPath(adminOptions);
        var packageText = File.Exists(aptPackagesPath)
            ? await File.ReadAllTextAsync(aptPackagesPath, cancellationToken)
            : string.Empty;
        var validation = ValidateAptPackages(packageText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredPackages = ParseAptPackages(packageText)
            .Select(NormalizeAptPackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();
        var hash = ComputeSha256(packageText);
        var appliedState = ReadGlobalAppliedState(adminOptions);
        var previouslyManagedPackages = ReadManagedAptPackages(appliedState);
        var packagesToRemove = previouslyManagedPackages
            .Except(desiredPackages, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();

        if (appliedState.TryGetValue("aptPackagesHash", out var previousHash)
            && string.Equals(previousHash, hash, StringComparison.Ordinal)
            && packagesToRemove.Length == 0)
        {
            return;
        }

        if (desiredPackages.Length == 0 && packagesToRemove.Length == 0)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("apt package apply is supported only on Linux containers.");
        }

        if (desiredPackages.Length > 0)
        {
            var update = await Cli.Wrap("apt-get")
                .WithArguments(args => args.Add("update"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
            if (update.ExitCode != 0)
            {
                throw new InvalidOperationException($"apt-get update failed with exit code {update.ExitCode}: {update.StandardError}");
            }

            var dryRun = await Cli.Wrap("apt-get")
                .WithArguments(args =>
                {
                    args.Add("install").Add("--dry-run").Add("-y").Add("--no-install-recommends");
                    foreach (var package in desiredPackages)
                    {
                        args.Add(package);
                    }
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);
            if (dryRun.ExitCode != 0)
            {
                throw new InvalidOperationException($"apt-get install dry-run failed with exit code {dryRun.ExitCode}: {dryRun.StandardError}");
            }
        }
    }

    public static async Task<AdminApplyResult> ApplyGlobalAptPackagesAsync(
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var aptPackagesPath = GetAptPackagesPath(adminOptions);
        var packageText = File.Exists(aptPackagesPath)
            ? await File.ReadAllTextAsync(aptPackagesPath, cancellationToken)
            : string.Empty;
        var validation = ValidateAptPackages(packageText);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        var desiredPackages = ParseAptPackages(packageText)
            .Select(NormalizeAptPackageName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();
        var hash = ComputeSha256(packageText);
        var appliedState = ReadGlobalAppliedState(adminOptions);
        var previouslyManagedPackages = ReadManagedAptPackages(appliedState);
        var packagesToRemove = previouslyManagedPackages
            .Except(desiredPackages, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static package => package, StringComparer.Ordinal)
            .ToArray();

        if (appliedState.TryGetValue("aptPackagesHash", out var previousHash)
            && string.Equals(previousHash, hash, StringComparison.Ordinal)
            && packagesToRemove.Length == 0)
        {
            return new AdminApplyResult("skipped", 0, 1, Array.Empty<string>());
        }

        if (desiredPackages.Length > 0 || packagesToRemove.Length > 0)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new InvalidOperationException("apt package apply is supported only on Linux containers.");
            }

            if (packagesToRemove.Length > 0)
            {
                var remove = await Cli.Wrap("apt-get")
                    .WithArguments(args =>
                    {
                        args.Add("remove").Add("-y");
                        foreach (var package in packagesToRemove)
                        {
                            args.Add(package);
                        }
                    })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);
                if (remove.ExitCode != 0)
                {
                    throw new InvalidOperationException($"apt-get remove failed with exit code {remove.ExitCode}: {remove.StandardError}");
                }
            }

            if (desiredPackages.Length > 0)
            {
                var update = await Cli.Wrap("apt-get")
                    .WithArguments(args => args.Add("update"))
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);
                if (update.ExitCode != 0)
                {
                    throw new InvalidOperationException($"apt-get update failed with exit code {update.ExitCode}: {update.StandardError}");
                }

                var install = await Cli.Wrap("apt-get")
                    .WithArguments(args =>
                    {
                        args.Add("install").Add("-y").Add("--no-install-recommends");
                        foreach (var package in desiredPackages)
                        {
                            args.Add(package);
                        }
                    })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken);
                if (install.ExitCode != 0)
                {
                    throw new InvalidOperationException($"apt-get install failed with exit code {install.ExitCode}: {install.StandardError}");
                }
            }
        }

        appliedState["version"] = "1";
        appliedState["aptPackagesHash"] = hash;
        appliedState["aptManagedPackages"] = string.Join('\n', desiredPackages);
        appliedState["aptPackagesAppliedAt"] = DateTimeOffset.UtcNow.ToString("O");
        await WriteGlobalAppliedStateAsync(adminOptions, appliedState, cancellationToken);
        logger.LogInformation(
            "Applied admin apt packages. installedCount={InstalledCount} removedCount={RemovedCount} hash={Hash}",
            desiredPackages.Length,
            packagesToRemove.Length,
            hash);
        return new AdminApplyResult("applied", 1, 0, Array.Empty<string>());
    }

    public static async Task<AdminApplyResult> ApplyAllKnownScopesAsync(
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var applied = 0;
        var skipped = 0;

        foreach (var scope in ScriptExecutionScopeRuntime.EnumerateExistingScopes(scopeOptions))
        {
            try
            {
                var result = await ScriptExecutionScopeRuntime.ApplyScopeRequirementsAsync(scope, scopeOptions, adminOptions, logger, cancellationToken);
                applied += result.ScopesApplied;
                skipped += result.ScopesSkipped;
            }
            catch (Exception ex)
            {
                var message = $"project={scope.ProjectId:D} guide={scope.GuideScopeId:D}: {ex.Message}";
                errors.Add(message);
                logger.LogWarning(ex, "Failed to apply scoped requirements. {Scope}", message);
            }
        }

        if (errors.Count > 0 && !adminOptions.FailOpen)
        {
            throw new InvalidOperationException($"One or more scoped requirement applies failed: {string.Join("; ", errors)}");
        }

        var status = errors.Count > 0 ? "partial" : applied > 0 ? "applied" : "skipped";
        return new AdminApplyResult(status, applied, skipped, errors.ToArray());
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IEnumerable<string> ParseAptPackages(string packageText) =>
        packageText.Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

    private static IReadOnlySet<string> ReadManagedAptPackages(IReadOnlyDictionary<string, string> state)
    {
        if (!state.TryGetValue("aptManagedPackages", out var serialized) || string.IsNullOrWhiteSpace(serialized))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ParseAptPackages(serialized)
            .Select(NormalizeAptPackageName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeAptPackageName(string packageName) => packageName.Trim().ToLowerInvariant();

    internal static Dictionary<string, string> ReadGlobalAppliedState(AdminApiOptions options)
    {
        var path = GetGlobalAppliedStatePath(options);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Task WriteGlobalAppliedStateAsync(
        AdminApiOptions options,
        Dictionary<string, string> state,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        return AtomicFile.WriteAllTextAsync(GetGlobalAppliedStatePath(options), json, cancellationToken);
    }
}

internal static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}

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
    public string? GuideId { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
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
