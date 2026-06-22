using System.Diagnostics;

namespace ScriptExecutionAgent.Tests.Infrastructure;

internal static class PythonVenvTestHelper
{
    /// <summary>
    /// Returns true only when a scoped venv can be created with a working pip install.
    /// Matches what ScriptExecutionAgent needs for scoped Python execution.
    /// </summary>
    internal static bool CanCreateScopedPythonVenv()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "python" }
            : new[] { "python3", "python" };

        foreach (var candidate in candidates)
        {
            var venvPath = Path.Combine(Path.GetTempPath(), "script-agent-venv-probe", Guid.NewGuid().ToString("N"));
            try
            {
                if (!TryCreateVenv(candidate, venvPath))
                {
                    continue;
                }

                var pythonExecutable = GetVenvPythonExecutable(venvPath);
                if (string.IsNullOrWhiteSpace(pythonExecutable) || !File.Exists(pythonExecutable))
                {
                    continue;
                }

                if (TryRunPythonModule(pythonExecutable, "pip", "--version"))
                {
                    return true;
                }
            }
            catch
            {
                // Try the next candidate.
            }
            finally
            {
                TryDeleteDirectory(venvPath);
            }
        }

        return false;
    }

    private static bool TryCreateVenv(string pythonCommand, string venvPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = pythonCommand,
            ArgumentList = { "-m", "venv", venvPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        return process is not null && process.WaitForExit(15000) && process.ExitCode == 0;
    }

    private static bool TryRunPythonModule(string pythonExecutable, string moduleName, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(moduleName);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        return process is not null && process.WaitForExit(15000) && process.ExitCode == 0;
    }

    private static string? GetVenvPythonExecutable(string venvPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(venvPath, "Scripts", "python.exe");
        }

        var unixPython = Path.Combine(venvPath, "bin", "python");
        return File.Exists(unixPython) ? unixPython : Path.Combine(venvPath, "bin", "python3");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
