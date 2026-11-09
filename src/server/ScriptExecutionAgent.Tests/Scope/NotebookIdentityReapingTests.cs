using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScriptExecutionAgent.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ScriptExecutionAgent.Tests.Scope;

[TestClass]
public sealed class NotebookIdentityReapingTests
{
    [TestCleanup]
    public void TearDown()
    {
        // Restore the default cap so a leaked low limit cannot evict identities in other tests.
        NotebookExecutionIdentityProvider.SetIdentityLimitForTests(512);
    }

    [TestMethod]
    public async Task Reap_removes_orphaned_user_and_group()
    {
        RequireLinuxIdentityTests();
        var fixture = new NotebookStorageFixture(MakeStorageRoot());
        try
        {
            var identity = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                fixture.ProjectId, fixture.NotebookId, NullLogger.Instance, CancellationToken.None);
            AssertUserExists(identity.UserName);

            Directory.Delete(fixture.NotebookRoot, recursive: true);
            await NotebookExecutionIdentityProvider.ReapOrphanedIdentitiesAsync(
                fixture.StorageRoot, NullLogger.Instance, CancellationToken.None);

            AssertUserMissing(identity.UserName);
            AssertGroupMissing(identity.GroupName);
        }
        finally
        {
            CleanCacheAndUser(fixture.ProjectId, fixture.NotebookId);
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task Reap_keeps_users_whose_notebook_exists()
    {
        RequireLinuxIdentityTests();
        var fixture = new NotebookStorageFixture(MakeStorageRoot());
        try
        {
            var identity = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                fixture.ProjectId, fixture.NotebookId, NullLogger.Instance, CancellationToken.None);

            await NotebookExecutionIdentityProvider.ReapOrphanedIdentitiesAsync(
                fixture.StorageRoot, NullLogger.Instance, CancellationToken.None);

            AssertUserExists(identity.UserName);
            AssertGroupExists(identity.GroupName);
        }
        finally
        {
            CleanCacheAndUser(fixture.ProjectId, fixture.NotebookId);
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task Reap_never_removes_a_cached_identity()
    {
        RequireLinuxIdentityTests();
        var storageRoot = MakeStorageRoot();
        var projectId = Guid.NewGuid();
        var notebookId = Guid.NewGuid();
        try
        {
            // A live (cached) identity whose notebook directory does not exist on disk must
            // survive the reaper: the in-memory cache is the authoritative "in use" signal.
            var identity = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectId, notebookId, NullLogger.Instance, CancellationToken.None);

            await NotebookExecutionIdentityProvider.ReapOrphanedIdentitiesAsync(
                storageRoot, NullLogger.Instance, CancellationToken.None);

            AssertUserExists(identity.UserName, "a cached identity is in use and must survive");
            AssertGroupExists(identity.GroupName);
        }
        finally
        {
            CleanCacheAndUser(projectId, notebookId);
            DeleteDirectorySafe(storageRoot);
        }
    }

    [TestMethod]
    public async Task Short_prefix_notebooks_get_distinct_users()
    {
        RequireLinuxIdentityTests();
        // Two notebook ids sharing the first 12 hex chars: under the legacy 12-hex naming
        // both would map to the same user (cross-notebook ownership sharing). This test
        // fails on the legacy naming and passes on the 24-hex naming.
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var notebookOne = new Guid("12345678aaaa11112222333344445556");
        var notebookTwo = new Guid("12345678aaaa99992222333344445556");
        notebookOne.ToString("N")[..12].Should().Be(notebookTwo.ToString("N")[..12]);
        try
        {
            var identityOne = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectA, notebookOne, NullLogger.Instance, CancellationToken.None);
            var identityTwo = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectB, notebookTwo, NullLogger.Instance, CancellationToken.None);

            identityOne.UserName.Should().Be($"nb_{notebookOne.ToString("N")[..24]}");
            identityOne.UserName.Should().NotBe(identityTwo.UserName);
            identityOne.Uid.Should().NotBe(identityTwo.Uid);
            AssertUserExists(identityOne.UserName);
            AssertUserExists(identityTwo.UserName);
        }
        finally
        {
            CleanCacheAndUser(projectA, notebookOne);
            CleanCacheAndUser(projectB, notebookTwo);
        }
    }

    [TestMethod]
    public async Task Lru_evicts_oldest_unused_at_cap()
    {
        RequireLinuxIdentityTests();
        NotebookExecutionIdentityProvider.SetIdentityLimitForTests(2);
        var projectA = Guid.NewGuid();
        var notebookA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var notebookB = Guid.NewGuid();
        var projectC = Guid.NewGuid();
        var notebookC = Guid.NewGuid();
        try
        {
            var identityA = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectA, notebookA, NullLogger.Instance, CancellationToken.None);
            await Task.Delay(25);
            var identityB = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectB, notebookB, NullLogger.Instance, CancellationToken.None);
            await Task.Delay(25);

            // Touch A so B becomes the oldest-unused.
            await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectA, notebookA, NullLogger.Instance, CancellationToken.None);
            await Task.Delay(25);

            // Creating C pushes the live count over the cap: the oldest-unused (B) is evicted
            // by the same call that serves C, exactly as PrepareAsync does per execution.
            var identityC = await NotebookExecutionIdentityProvider.GetOrCreateIdentityAsync(
                projectC, notebookC, NullLogger.Instance, CancellationToken.None);

            AssertUserExists(identityA.UserName, "A was recently used and must survive");
            AssertUserMissing(identityB.UserName, "B was the oldest-unused and must be evicted");
            AssertGroupMissing(identityB.GroupName);
            AssertUserExists(identityC.UserName);
            NotebookExecutionIdentityProvider.IdentityCache
                .Should().ContainKeys($"{projectA:D}:{notebookA:D}", $"{projectC:D}:{notebookC:D}");
            NotebookExecutionIdentityProvider.IdentityCache.Should().HaveCount(2);
        }
        finally
        {
            CleanCacheAndUser(projectA, notebookA);
            CleanCacheAndUser(projectB, notebookB);
            CleanCacheAndUser(projectC, notebookC);
        }
    }

    [TestMethod]
    public async Task Reap_handles_legacy_short_names()
    {
        RequireLinuxIdentityTests();
        // A pre-upgrade container carries nb_<12hex> users. The reaper must keep one whose
        // notebook still exists (matched by 12-hex suffix) and reap one whose notebook is
        // gone - so an upgrade never deletes a still-live notebook's user.
        var storageRoot = MakeStorageRoot();
        var survivingNotebookId = new Guid("fedcba98765433334444555566667778");
        var orphanedSuffix = Guid.NewGuid().ToString("N")[..12];
        Directory.CreateDirectory(
            Path.Combine(storageRoot, $"project-{Guid.NewGuid():N}", $"notebook-{survivingNotebookId:N}"));
        try
        {
            var survivingUserName = $"nb_{survivingNotebookId.ToString("N")[..12]}";
            var survivingGroupName = $"nbg_{survivingNotebookId.ToString("N")[..12]}";
            var orphanedUserName = $"nb_{orphanedSuffix}";
            var orphanedGroupName = $"nbg_{orphanedSuffix}";
            CreateLegacyIdentity(survivingUserName, survivingGroupName);
            CreateLegacyIdentity(orphanedUserName, orphanedGroupName);

            await NotebookExecutionIdentityProvider.ReapOrphanedIdentitiesAsync(
                storageRoot, NullLogger.Instance, CancellationToken.None);

            AssertUserExists(survivingUserName, "a legacy name with a matching notebook must survive");
            AssertGroupExists(survivingGroupName);
            AssertUserMissing(orphanedUserName, "a legacy name with no notebook must be reaped");
            AssertGroupMissing(orphanedGroupName);
        }
        finally
        {
            RunProcessLenient("userdel", $"nb_{survivingNotebookId.ToString("N")[..12]}");
            RunProcessLenient("groupdel", $"nbg_{survivingNotebookId.ToString("N")[..12]}");
            DeleteDirectorySafe(storageRoot);
        }
    }

    private static string MakeStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "script-agent-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDirectorySafe(string path)
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

    private static void CreateLegacyIdentity(string userName, string groupName)
    {
        RunProcess("groupadd", $"--system {groupName}");
        RunProcess("useradd", $"--system --gid {groupName} --home /nonexistent --shell /usr/sbin/nologin {userName}");
        AssertUserExists(userName, "legacy identity setup must have created the user");
    }

    private static void CleanCacheAndUser(Guid projectId, Guid notebookId)
    {
        var cacheKey = $"{projectId:D}:{notebookId:D}";
        if (NotebookExecutionIdentityProvider.IdentityCache.TryRemove(cacheKey, out var identity))
        {
            NotebookExecutionIdentityProvider.IdentityLastUse.TryRemove(cacheKey, out _);
            RunProcessLenient("userdel", identity.UserName);
            RunProcessLenient("groupdel", identity.GroupName);
        }
    }

    private static int RunProcessStart(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} failed to start");
        process.WaitForExit(10_000);
        return process.ExitCode;
    }

    private static void RunProcess(string fileName, string arguments)
    {
        RunProcessStart(fileName, arguments).Should().Be(0, $"{fileName} {arguments} should succeed");
    }

    private static void RunProcessLenient(string fileName, string arguments)
    {
        try
        {
            RunProcessStart(fileName, arguments);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void AssertUserExists(string userName, string? because = null)
    {
        RunProcessStart("id", $"-u {userName}").Should().Be(0, because ?? $"user {userName} should exist");
    }

    private static void AssertUserMissing(string userName, string? because = null)
    {
        RunProcessStart("id", $"-u {userName}").Should().NotBe(0, because ?? $"user {userName} should not exist");
    }

    private static void AssertGroupExists(string groupName, string? because = null)
    {
        RunProcessStart("getent", $"group {groupName}").Should().Be(0, because ?? $"group {groupName} should exist");
    }

    private static void AssertGroupMissing(string groupName, string? because = null)
    {
        RunProcessStart("getent", $"group {groupName}").Should().NotBe(0, because ?? $"group {groupName} should not exist");
    }

    private static void RequireLinuxIdentityTests()
    {
        if (!OperatingSystem.IsLinux() || !MountTestHelper.CanUseLinuxIdentityIsolation)
        {
            Assert.Inconclusive("Linux with root identity management (groupadd/useradd) is required for these tests.");
        }

        // GetOrCreateIdentityAsync warms setpriv; without it every identity test fails for
        // the wrong reason.
        RunProcessStart(
            "setpriv",
            "--reuid 0 --regid 0 --init-groups -- true").Should().Be(0, "setpriv must be usable for identity tests");
    }
}
