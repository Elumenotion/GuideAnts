using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class PackageRunnerEnvironmentIsolationTests
{
    private const string ProbeVariable = "GA_NPX_ISOLATION_PROBE";

    [TestCleanup]
    public void TearDown() => Environment.SetEnvironmentVariable(ProbeVariable, null);

    [TestMethod]
    public async Task Npx_node_grandchild_does_not_inherit_container_environment()
    {
        if (!IsToolAvailable("npx") || !IsToolAvailable("node"))
        {
            Assert.Inconclusive("npx and node are required for this test.");
        }

        Environment.SetEnvironmentVariable(ProbeVariable, "container-secret");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN", "agent-token");

        try
        {
            var scopedEnvironment = new Dictionary<string, string?>
            {
                ["HOME"] = Path.Combine(Path.GetTempPath(), "guideants-npx-isolation-test"),
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
                ["LANG"] = "C.UTF-8",
                ["LC_ALL"] = "C.UTF-8",
            };

            var launch = ScriptExecutionScopeRuntime.BuildIsolatedProcessLaunchPlan(
                "npx",
                ["--yes", "node", "-e", "process.stdout.write(JSON.stringify(process.env))"],
                scopedEnvironment);

            var startInfo = new ProcessStartInfo
            {
                FileName = launch.Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in launch.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment.Clear();
            if (launch.ProcessEnvironment is not null)
            {
                foreach (var (key, value) in launch.ProcessEnvironment)
                {
                    if (value is not null)
                    {
                        startInfo.Environment[key] = value;
                    }
                }
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start isolated npx launch.");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, because: stderr);

            var env = JsonSerializer.Deserialize<Dictionary<string, string>>(stdout)
                ?? throw new InvalidOperationException("npx/node env probe returned invalid JSON.");

            env.Should().NotContainKey(ProbeVariable);
            env.Should().NotContainKey("SCRIPT_EXECUTION_AGENT_TOKEN");
            env.Should().NotContainKey("GA_TTS_HOST");
            env.Values.Should().NotContain(string.Empty);
            env.Should().ContainKey("HOME");
            env.Should().ContainKey("PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProbeVariable, null);
            Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN", null);
        }
    }

    private static bool IsToolAvailable(string toolName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return false;
        }

        foreach (var directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), toolName);
            if (File.Exists(candidate))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".cmd"))
            {
                return true;
            }
        }

        return false;
    }
}
