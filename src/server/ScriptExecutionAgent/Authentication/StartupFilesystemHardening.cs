using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using GuideAnts.Logging;
namespace ScriptExecutionAgent;

internal static class StartupFilesystemHardening
{
    public static async Task ApplyAsync(string fileStorageRoot, ILogger logger, string? baseDirectory = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The published app's base directory is /app/script-agent in every container image
        // (COPY ScriptExecutionAgent/ /app/script-agent/ + the entrypoint launches
        // /app/script-agent/ScriptExecutionAgent.dll), so the app directory self-locates
        // instead of hardcoding the container path.
        var appDirectory = Path.TrimEndingDirectorySeparator(baseDirectory ?? AppContext.BaseDirectory);
        var fullStorageRoot = Path.GetFullPath(fileStorageRoot);
        await BestEffortCommandAsync("chmod", new[] { "751", fullStorageRoot }, logger, "chmod FILE_STORAGE_ROOT");
        await BestEffortCommandAsync("chown", new[] { "-R", "root:root", appDirectory }, logger, "chown script-agent");
        await BestEffortCommandAsync("chmod", new[] { "-R", "go-rwx", appDirectory }, logger, "chmod script-agent");
    }

    internal static async Task BestEffortCommandAsync(string fileName, IReadOnlyCollection<string> args, ILogger logger, string description)
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
