using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using GuideAnts.Logging;
namespace ScriptExecutionAgent;

internal static class StartupFilesystemHardening
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

