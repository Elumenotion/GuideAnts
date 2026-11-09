using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ScriptExecutionAgent.Tests.Scope;

[TestClass]
public sealed class StartupHardeningTests
{
    private const int DirMode700 = 7 * 64 + 0 * 8 + 0;
    private const int StorageRootMode751 = 7 * 64 + 5 * 8 + 1;

    [SupportedOSPlatform("linux")]
    [TestMethod]
    public async Task ApplyAsync_hides_app_dir_from_others()
    {
        RequireChownToRoot();
        var testRoot = CreateTestRoot();
        try
        {
            var storageRoot = Path.Combine(testRoot, "storage");
            var appDirectory = Path.Combine(testRoot, "app");
            Directory.CreateDirectory(storageRoot);
            var innerDirectory = Directory.CreateDirectory(Path.Combine(appDirectory, "bin"));
            var innerFile = Path.Combine(innerDirectory.FullName, "agent.dll");
            File.WriteAllText(innerFile, "payload");
            File.SetUnixFileMode(innerFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            await StartupFilesystemHardening.ApplyAsync(storageRoot, NullLogger.Instance, appDirectory);

            ((int)File.GetUnixFileMode(appDirectory)).Should().Be(DirMode700);
            ReadOwnership(appDirectory).Should().Be((0, 0), "the app directory must be root:root");
            ((int)File.GetUnixFileMode(innerFile)).Should().Be(DirMode700, "the app directory must lose group/other access");
            ReadOwnership(innerFile).Should().Be((0, 0));
            ((int)File.GetUnixFileMode(storageRoot)).Should().Be(StorageRootMode751);
        }
        finally
        {
            CleanupTestRoot(testRoot);
        }
    }

    [TestMethod]
    public async Task ApplyAsync_is_best_effort_when_commands_fail()
    {
        var testRoot = CreateTestRoot();
        try
        {
            // Non-existent targets: every hardening command fails (non-zero exit) and the
            // method must only log warnings, never throw - startup must survive on machines
            // where the hardening cannot apply (e.g. a local dev machine without root).
            var missingStorageRoot = Path.Combine(testRoot, "missing-storage");
            var missingAppDirectory = Path.Combine(testRoot, "missing-app");

            Func<Task> act = async () =>
                await StartupFilesystemHardening.ApplyAsync(missingStorageRoot, NullLogger.Instance, missingAppDirectory);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            CleanupTestRoot(testRoot);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "script-agent-hardening", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupTestRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void RequireChownToRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Startup hardening tests require Linux.");
        }

        var probe = Path.Combine(Path.GetTempPath(), $"hardening-chown-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "probe");
            RunShell("chown", "root:root", probe);
            ReadOwnership(probe).Should().Be((0, 0), "chown to root requires elevated privileges");
        }
        catch (Exception)
        {
            Assert.Inconclusive("Startup hardening requires chown to root:run; this host cannot do it.");
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private static (int Uid, int Gid) ReadOwnership(string path)
    {
        var output = RunShell("stat", "-c '%u %g'", path);
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        parts.Length.Should().Be(2);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static string RunShell(string fileName, string arguments, string? extra = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = extra is null ? arguments : $"{arguments} {extra}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} failed to start");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        }

        return output;
    }
}
