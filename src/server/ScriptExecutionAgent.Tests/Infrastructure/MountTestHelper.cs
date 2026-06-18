using System.Diagnostics;
using System.Text.Json;

namespace ScriptExecutionAgent.Tests.Infrastructure;

public static class MountTestHelper
{
    public static bool CanCreateDirectoryLinks { get; }

    static MountTestHelper()
    {
        CanCreateDirectoryLinks = TryCreateProbeLink();
    }

    public static string CreateHostMountsRoot(string parentDirectory)
    {
        var hostMountsRoot = Path.Combine(parentDirectory, "HostMounts");
        Directory.CreateDirectory(hostMountsRoot);
        return hostMountsRoot;
    }

    public static string CreateContainerSource(string hostMountsRoot, string mountKey)
    {
        var containerSource = Path.Combine(hostMountsRoot, mountKey);
        Directory.CreateDirectory(containerSource);
        return containerSource;
    }

    public static void WriteMountsRegistry(
        NotebookStorageFixture notebook,
        IReadOnlyList<MountRegistryEntry> mounts)
    {
        var registry = new
        {
            schemaVersion = 1,
            mounts = mounts.Select(m => new
            {
                mountId = m.MountId,
                leafName = m.LeafName,
                linkRelativePath = m.LinkRelativePath,
                containerSourcePath = m.ContainerSourcePath,
                writable = m.Writable
            })
        };

        var metadataDir = Path.Combine(notebook.NotebookRoot, ".guideants");
        Directory.CreateDirectory(metadataDir);
        File.WriteAllText(
            Path.Combine(metadataDir, "mounts.json"),
            JsonSerializer.Serialize(registry));
    }

    public static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            throw new InvalidOperationException($"Link path already exists: {linkPath}");
        }

        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start mklink for junction creation.");
            process.WaitForExit(10_000);
            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"mklink /J failed with exit code {process.ExitCode}: {stderr}");
            }

            return;
        }

        Directory.CreateSymbolicLink(linkPath, targetPath);
    }

    public static RegisteredMountFixture CreateRegisteredMount(
        NotebookStorageFixture notebook,
        string hostMountsRoot,
        string leafName,
        string mountKey,
        bool writable)
    {
        var containerSourcePath = CreateContainerSource(hostMountsRoot, mountKey);
        var linkPath = Path.Combine(notebook.NotebookRoot, leafName);
        CreateDirectoryLink(linkPath, containerSourcePath);

        var entry = new MountRegistryEntry(
            MountId: Guid.NewGuid().ToString(),
            LeafName: leafName,
            LinkRelativePath: leafName,
            ContainerSourcePath: containerSourcePath,
            Writable: writable);

        WriteMountsRegistry(notebook, [entry]);

        return new RegisteredMountFixture(
            entry,
            linkPath,
            containerSourcePath,
            Path.Combine(notebook.NotebookRoot, leafName));
    }

    public static void CreateUnregisteredDirectoryLink(
        NotebookStorageFixture notebook,
        string hostMountsRoot,
        string leafName,
        string mountKey)
    {
        var containerSourcePath = CreateContainerSource(hostMountsRoot, mountKey);
        var linkPath = Path.Combine(notebook.NotebookRoot, leafName);
        CreateDirectoryLink(linkPath, containerSourcePath);
    }

    private static bool TryCreateProbeLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "script-agent-link-probe", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            CreateDirectoryLink(link, target);
            return Directory.Exists(link);
        }
        catch
        {
            return false;
        }
        finally
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
    }
}

public sealed record MountRegistryEntry(
    string MountId,
    string LeafName,
    string LinkRelativePath,
    string ContainerSourcePath,
    bool Writable);

public sealed record RegisteredMountFixture(
    MountRegistryEntry Entry,
    string LinkPath,
    string ContainerSourcePath,
    string NotebookScopedPath);
