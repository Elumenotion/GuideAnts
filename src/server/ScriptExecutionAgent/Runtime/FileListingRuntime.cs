using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using GuideAnts.Logging;
namespace ScriptExecutionAgent;

/// <summary>
/// Directory listing for /files, in-process or via setpriv under the notebook identity.
/// </summary>
internal static class FileListingRuntime
{
    internal static async Task<string[]> ListFilesAsync(
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
            if (ScriptProcessRuntime.IsTemporaryScriptFile(name))
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

    internal static async Task<List<string>> ListFilesViaSetprivAsync(
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
}
