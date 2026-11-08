using CliWrap;
using CliWrap.Buffered;
using GuideAnts.Logging;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScriptExecutionAgent;

internal static class ScriptExecutionScopeRuntime
{
    internal static readonly ConcurrentDictionary<string, SemaphoreSlim> VenvLocks = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Regex EnvironmentVariableNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    internal static readonly Regex RequirementsPackageNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    internal static readonly Regex NormalizedPythonPackageSeparatorPattern = new("[-_.]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    internal static readonly HashSet<string> ProtectedTopLevelPythonPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "pip",
        "setuptools",
        "wheel"
    };
    internal static readonly HashSet<string> ReservedEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCRIPT_EXECUTION_SCOPE_ROOT",
        "SCRIPT_EXECUTION_SCOPE_PROJECT_ID",
        "SCRIPT_EXECUTION_SCOPE_GUIDE_ID",
        "SCRIPT_EXECUTION_SCOPE_CREDENTIALS_FILE",
        "PATH",
        "HOME",
        "XDG_CACHE_HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "NPM_CONFIG_CACHE",
        "npm_config_cache",
        "UV_CACHE_DIR",
        "PIP_CACHE_DIR",
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

    internal static readonly string[] AllowedInheritedEnvironmentKeys =
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

        var projectRoot = Path.Combine(options.StateRootPath, $"project-{projectId:N}");
        var scopeRoot = Path.Combine(projectRoot, $"guide-{guideScopeId:N}");
        var projectRuntimeRoot = Path.Combine(projectRoot, "runtime");

        return new ScriptExecutionScope(
            projectId,
            guideScopeId,
            scopeRoot,
            projectRuntimeRoot,
            Path.Combine(scopeRoot, venvRelativePath),
            Path.Combine(scopeRoot, "requirements.txt"),
            Path.Combine(scopeRoot, "applied-state.json"));
    }

    public static void EnsureProjectRuntimeDirectory(ScriptExecutionScope scope)
    {
        Directory.CreateDirectory(scope.ProjectRuntimeRootPath);
        Directory.CreateDirectory(Path.Combine(scope.ProjectRuntimeRootPath, "cache"));
        Directory.CreateDirectory(Path.Combine(scope.ProjectRuntimeRootPath, "config"));
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

    /// <summary>
    /// Ensures the scope's Python venv exists, creating it if necessary. Returns true only when
    /// this call actually created a brand-new venv (durable volume was empty/lost); false when an
    /// existing venv was reused as-is. Callers use this to distinguish "just provisioned from
    /// scratch" from "already-established venv that a script legitimately modified" so on-demand
    /// execution can self-heal the former without silently reversing the latter.
    /// </summary>
    public static async Task<bool> EnsurePythonVenvAsync(
        ScriptExecutionScope scope,
        ScriptExecutionScopeOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (File.Exists(scope.PythonExecutablePath))
        {
            EnsureBasePythonRuntimeExtension(scope, options, logger);
            return false;
        }

        var venvLock = VenvLocks.GetOrAdd(scope.PythonVenvPath, static _ => new SemaphoreSlim(1, 1));
        await venvLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(scope.PythonExecutablePath))
            {
                EnsureBasePythonRuntimeExtension(scope, options, logger);
                return false;
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
                        return true;
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
                        return true;
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

    internal static async Task<(bool Success, string Detail)> TryCreatePythonVenvAsync(
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

    internal static async Task<(bool Success, string Detail)> TryBootstrapScopedVenvPipAsync(
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

    internal static void TryDeletePythonVenvDirectory(string venvPath)
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

    internal static void EnsureBasePythonRuntimeExtension(
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
        var venvJustCreated = await EnsurePythonVenvAsync(scope, scopeOptions, logger, cancellationToken);

        // Serialize requirement installs per venv so a background startup reconcile and an
        // on-demand execution never run two pip processes against the same environment.
        var venvLock = VenvLocks.GetOrAdd(scope.PythonVenvPath, static _ => new SemaphoreSlim(1, 1));
        await venvLock.WaitAsync(cancellationToken);
        try
        {
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
            var scopedSitePackagesPath = ResolveScopedSitePackagesPath(scope.PythonVenvPath);
            // Only check for packages missing from an already-established venv when it was just
            // (re)provisioned from scratch this call - e.g. the durable volume was empty/lost.
            // Once a venv exists, a package a script legitimately removed via its own pip call
            // must stay removed until an explicit /admin/apply, not get silently reinstalled on
            // the very next /execute.
            var missingTopLevelPackages = venvJustCreated
                ? await GetMissingTopLevelPackagesAsync(
                    scope.PythonExecutablePath,
                    scopedSitePackagesPath,
                    desiredTopLevelPackages,
                    cancellationToken)
                : Array.Empty<string>();
            if (requirementsHash == appliedState.RequirementsHash && missingTopLevelPackages.Count == 0)
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
                scopedSitePackagesPath,
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
        finally
        {
            venvLock.Release();
        }
    }

    public static async Task<AdminApplyResult> ApplyScopeRequirementsAsync(
        ScriptExecutionScope scope,
        AdminApplyTargets targets,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        EnsureScopeDirectory(scope);
        await EnsurePythonVenvAsync(scope, scopeOptions, logger, cancellationToken);

        // Serialize requirement installs per venv so a background startup reconcile and an
        // on-demand execution never run two pip processes against the same environment.
        var venvLock = VenvLocks.GetOrAdd(scope.PythonVenvPath, static _ => new SemaphoreSlim(1, 1));
        await venvLock.WaitAsync(cancellationToken);
        try
        {
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
            var scopedSitePackagesPath = ResolveScopedSitePackagesPath(scope.PythonVenvPath);
            var missingTopLevelPackages = await GetMissingTopLevelPackagesAsync(
                scope.PythonExecutablePath,
                scopedSitePackagesPath,
                desiredTopLevelPackages,
                cancellationToken);
            var unmanagedPackagesBeforeInstall = await GetUnmanagedTopLevelPackagesAsync(
                scope.PythonExecutablePath,
                scopedSitePackagesPath,
                desiredTopLevelPackages,
                cancellationToken);
            var installScriptsDocument = AdminInstallScriptsRuntime.ReadDocument(scope);
            var installScriptsHash = AdminInstallScriptsRuntime.ComputeDocumentHash(installScriptsDocument);
            var pipInstallNeeded = requirementsHash != appliedState.RequirementsHash || missingTopLevelPackages.Count > 0;
            var requirementsNeedsApply = targets.Pip
                && (pipInstallNeeded || unmanagedPackagesBeforeInstall.Count > 0);
            var scriptsNeedApply = targets.InstallScripts
                && AdminInstallScriptsRuntime.NeedsApply(
                    installScriptsHash,
                    appliedState.InstallScriptsHash,
                    installScriptsDocument.Scripts.Count);
            if (!requirementsNeedsApply && !scriptsNeedApply)
            {
                return new AdminApplyResult("skipped", 0, 1, Array.Empty<string>());
            }

            if (requirementsNeedsApply && pipInstallNeeded && !string.IsNullOrWhiteSpace(requirementsText))
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
                    scopedSitePackagesPath,
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
                targets.Pip && requirementsNeedsApply ? requirementsHash : appliedState.RequirementsHash,
                targets.Pip && requirementsNeedsApply ? requirementsPath : appliedState.RequirementsPath,
                targets.Pip && requirementsNeedsApply
                    ? desiredTopLevelPackages
                    : appliedState.TopLevelPackages,
                targets.InstallScripts && scriptsNeedApply ? installScriptsHash : appliedState.InstallScriptsHash,
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
        finally
        {
            venvLock.Release();
        }
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

    internal static (string FileName, string[] Arguments) ApplyInstallScriptPrivacyWrapper(string commandFile, string[] commandArgs)
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

    internal static async Task<ScriptProcessResult> ExecuteInstallScriptProcessAsync(
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
        AdminApplyTargets targets,
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
        var scopedSitePackagesPath = ResolveScopedSitePackagesPath(scope.PythonVenvPath);
        var missingTopLevelPackages = await GetMissingTopLevelPackagesAsync(
            scope.PythonExecutablePath,
            scopedSitePackagesPath,
            desiredTopLevelPackages,
            cancellationToken);
        var unmanagedPackagesBeforeInstall = await GetUnmanagedTopLevelPackagesAsync(
            scope.PythonExecutablePath,
            scopedSitePackagesPath,
            desiredTopLevelPackages,
            cancellationToken);
        var pipInstallNeeded = requirementsHash != appliedState.RequirementsHash || missingTopLevelPackages.Count > 0;
        if (targets.Pip)
        {
            if (pipInstallNeeded || unmanagedPackagesBeforeInstall.Count > 0)
            {
                if (pipInstallNeeded && !string.IsNullOrWhiteSpace(requirementsText))
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
        }

        if (targets.InstallScripts)
        {
            var installScriptsDocument = AdminInstallScriptsRuntime.ReadDocument(scope);
            await AdminInstallScriptsRuntime.PreflightSyntaxAsync(
                scope,
                scopeOptions,
                installScriptsDocument,
                logger,
                cancellationToken);
        }
    }

    internal static string FormatPipFailure(BufferedCommandResult result)
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
        EnsureProjectRuntimeDirectory(scope);
        var projectRuntimeRoot = scope.ProjectRuntimeRootPath;
        var cacheRoot = Path.Combine(projectRuntimeRoot, "cache");

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = BuildDefaultPath(scope),
            ["HOME"] = projectRuntimeRoot,
            ["XDG_CACHE_HOME"] = cacheRoot,
            ["XDG_CONFIG_HOME"] = Path.Combine(projectRuntimeRoot, "config"),
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

        return environment;
    }

    /// <summary>
    /// Builds a process launch plan that matches <c>/execute</c> isolation. On Linux this uses
    /// <c>env -i</c> so package runners cannot inherit ScriptExecutionAgent container variables.
    /// </summary>
    public static IsolatedProcessLaunchPlan BuildIsolatedProcessLaunchPlan(
        string commandFile,
        IReadOnlyList<string> commandArgs,
        IReadOnlyDictionary<string, string?> scopedEnvironment)
    {
        var curated = BuildTransportEnvironment(scopedEnvironment);

        if (OperatingSystem.IsLinux())
        {
            var envExecutable = ResolveEnvExecutable();
            if (envExecutable is not null)
            {
                var envArgs = new List<string>(curated.Count + commandArgs.Count + 2) { "-i" };
                foreach (var (key, value) in curated)
                {
                    envArgs.Add($"{key}={value}");
                }

                envArgs.Add(commandFile);
                envArgs.AddRange(commandArgs);
                return new IsolatedProcessLaunchPlan(envExecutable, envArgs.ToArray(), null);
            }
        }

        return new IsolatedProcessLaunchPlan(commandFile, commandArgs.ToArray(), curated);
    }

    internal static string? ResolveEnvExecutable() =>
        File.Exists("/usr/bin/env") ? "/usr/bin/env" : null;

    /// <summary>
    /// Builds the MCP stdio child environment to match <c>/execute</c>: only the curated
    /// scoped variables, with no ScriptExecutionAgent container inheritance.
    /// </summary>
    public static Dictionary<string, string?> BuildTransportEnvironment(
        IReadOnlyDictionary<string, string?> scopedEnvironment)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in scopedEnvironment)
        {
            if (value is not null)
            {
                environment[key] = value;
            }
        }

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

    internal static string BuildDefaultPath(ScriptExecutionScope scope)
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

    internal static IEnumerable<KeyValuePair<string, string>> GetAllowedInheritedEnvironment()
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

    internal static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static IReadOnlySet<string> ParseTopLevelRequirementPackageNames(string requirementsText)
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

    internal static string? ResolveScopedSitePackagesPath(string pythonVenvPath) =>
        FindPythonSitePackages(pythonVenvPath).FirstOrDefault();

    internal static async Task<IReadOnlyList<string>> GetMissingTopLevelPackagesAsync(
        string pythonExecutablePath,
        string? scopedSitePackagesPath,
        IReadOnlySet<string> desiredTopLevelPackages,
        CancellationToken cancellationToken)
    {
        if (desiredTopLevelPackages.Count == 0)
        {
            return Array.Empty<string>();
        }

        var installedTopLevelPackages = await GetInstalledTopLevelPackagesAsync(
            pythonExecutablePath,
            scopedSitePackagesPath,
            cancellationToken);
        return desiredTopLevelPackages
            .Where(packageName => !installedTopLevelPackages.Contains(packageName))
            .OrderBy(static packageName => packageName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static async Task<IReadOnlyList<string>> GetUnmanagedTopLevelPackagesAsync(
        string pythonExecutablePath,
        string? scopedSitePackagesPath,
        IReadOnlySet<string> desiredTopLevelPackages,
        CancellationToken cancellationToken)
    {
        var installedTopLevelPackages = await GetInstalledTopLevelPackagesAsync(
            pythonExecutablePath,
            scopedSitePackagesPath,
            cancellationToken);
        return installedTopLevelPackages
            .Where(packageName =>
                !ProtectedTopLevelPythonPackages.Contains(packageName)
                && !desiredTopLevelPackages.Contains(packageName))
            .OrderBy(static packageName => packageName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static async Task<IReadOnlyList<string>> PruneUnmanagedTopLevelPackagesAsync(
        string pythonExecutablePath,
        string? scopedSitePackagesPath,
        IReadOnlySet<string> desiredTopLevelPackages,
        CancellationToken cancellationToken)
    {
        var removedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var toRemove = await GetUnmanagedTopLevelPackagesAsync(
                pythonExecutablePath,
                scopedSitePackagesPath,
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

    internal static async Task<IReadOnlySet<string>> GetInstalledTopLevelPackagesAsync(
        string pythonExecutablePath,
        string? scopedSitePackagesPath,
        CancellationToken cancellationToken)
    {
        var listResult = await Cli.Wrap(pythonExecutablePath)
            .WithArguments(args =>
            {
                args.Add("-m")
                    .Add("pip")
                    .Add("--disable-pip-version-check")
                    .Add("list")
                    .Add("--not-required")
                    .Add("--format=json");
                if (!string.IsNullOrWhiteSpace(scopedSitePackagesPath))
                {
                    args.Add("--path").Add(scopedSitePackagesPath);
                }
            })
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

    internal static string NormalizePythonPackageName(string packageName) =>
        NormalizedPythonPackageSeparatorPattern.Replace(packageName.Trim().ToLowerInvariant(), "-");

    internal static string NormalizeRelativePath(string? candidate, string fallback)
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

    internal static IReadOnlyList<string> FindPythonSitePackages(string venvPath)
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

    internal static IReadOnlyList<string> GetPythonBootstrapCommands(string? configuredBootstrapCommand)
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

