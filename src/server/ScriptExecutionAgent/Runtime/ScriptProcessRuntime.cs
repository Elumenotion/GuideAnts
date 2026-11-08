using CliWrap;
using CliWrap.Buffered;
using GuideAnts.Logging;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
namespace ScriptExecutionAgent;

/// <summary>
/// Script temp-file lifecycle, process launch (direct and setpriv-isolated), and command selection.
/// Extracted verbatim from Program.cs; captured locals were promoted to parameters.
/// </summary>
internal static class ScriptProcessRuntime
{
    internal static bool IsTemporaryScriptFile(string filename)
    {
        var pattern = @"^[a-f0-9]{32}_script\.(sh|ps1|py)$";
        return Regex.IsMatch(filename, pattern, RegexOptions.IgnoreCase);
    }

    internal static async Task<ScriptExecutionResult> ExecuteScriptAsync(
        ScriptExecutionRequest request,
        ScriptExecutionConfig config,
        ILogger logger,
        NotebookExecutionIdentity? executionIdentity,
        ScriptExecutionScopeOptions scopeOptions,
        AdminApiOptions adminOptions,
        System.Diagnostics.Stopwatch executionStopwatch,
        CancellationToken requestAborted = default)
    {
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();
        HashSet<string> preExistingFiles = new(StringComparer.OrdinalIgnoreCase);
        var preSnapshotSucceeded = false;
        int? exitCode = null;
        var injectedEnvEntries = request.Environment?.Count ?? 0;
        var timedOut = false;
        var cancelledByCaller = false;

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
            await File.WriteAllTextAsync(scriptFilePath, request.Script, requestAborted);

            if (executionIdentity is not null && OperatingSystem.IsLinux())
            {
                try
                {
                    await NotebookExecutionIdentityProvider.PrepareScriptFileAsync(scriptFilePath, executionIdentity, requestAborted);
                }
                catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "SECURITY: failed to apply notebook identity ownership to script file {ScriptFilePath}", LogValueSanitizer.Sanitize(scriptFilePath));
                }
            }

            var scope = ScriptExecutionScopeRuntime.ResolveScope(
                Guid.Parse(request.ProjectId),
                ExecutionRequestValidator.ResolveGuideScopeId(request),
                scopeOptions);
            ScriptExecutionScopeRuntime.EnsureScopeDirectory(scope);
            if (request.ScriptType == ScriptType.Python)
            {
                try
                {
                    // EnsureScopeRequirementsForExecutionAsync ensures the venv itself internally; a
                    // separate EnsurePythonVenvAsync call here would consume the "just created" signal
                    // before it gets there, defeating its just-provisioned-vs-already-established check.
                    await ScriptExecutionScopeRuntime.EnsureScopeRequirementsForExecutionAsync(scope, scopeOptions, adminOptions, logger, requestAborted);
                }
                catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
                {
                    throw;
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            cts.CancelAfter(ExecutionRequestValidator.ResolveExecutionTimeout(request, config));
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
            exitCode = run.ExitCode;

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
            if (requestAborted.IsCancellationRequested)
            {
                cancelledByCaller = true;
                stdErrBuffer.AppendLine("Script execution was cancelled");
            }
            else
            {
                timedOut = true;
                stdErrBuffer.AppendLine("Script execution timed out");
            }
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

        var guideId = ExecutionRequestValidator.ResolveGuideScopeId(request);
        AgentLogEvent.Emit("script_execution_complete", new Dictionary<string, object?>
        {
            ["projectId"] = request.ProjectId,
            ["notebookId"] = request.NotebookId,
            ["guideId"] = guideId.ToString("D"),
            ["scriptType"] = request.ScriptType.ToString(),
            ["durationMs"] = executionStopwatch.ElapsedMilliseconds,
            ["exitCode"] = exitCode,
            ["success"] = exitCode == 0,
            ["preExistingFileCount"] = preExistingFiles.Count,
            ["injectedEnvEntries"] = injectedEnvEntries,
            ["timedOut"] = timedOut,
            ["cancelledByCaller"] = cancelledByCaller,
        });

        return new ScriptExecutionResult
        {
            StandardOutput = cleanedOutput,
            StandardError = cleanedError,
            ExitCode = exitCode
        };
    }

    internal static async Task<ScriptProcessResult> ExecuteScriptWithSetprivAsync(
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

    internal static async Task<ScriptProcessResult> RunScriptProcessAsync(
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

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ScriptProcessResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    internal static (string FileName, string[] Arguments) ApplyPrivacyWrapper(string commandFile, string[] commandArgs)
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

    internal static (string FileName, string[] Arguments) GetScriptCommand(
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
}
