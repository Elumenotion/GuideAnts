using FluentAssertions;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Tests.Architecture;

/// <summary>T-A4 — locks F-1 against a future over-engineer: no pending-delete,
/// deferred-replay, or queued disk-cleanup concept exists for local model deletion.</summary>
[TestClass]
public sealed partial class NoDeferredDiskCleanupContractTests
{
    [TestMethod]
    public void ServerTree_HasNoDeferredDiskCleanupConcept()
    {
        var serverDir = Path.Combine(FindServerRoot(), "GuideAntsApi");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(FindServerRoot(), file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (DeferredCleanupRegex().IsMatch(text))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty(
            "F-1: deletion is immediate-or-accepted-loss; nothing queued, nothing replayed: {0}",
            string.Join(Environment.NewLine, offenders));
    }

    private static string FindServerRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "GuideAntsApi"))
                && Directory.Exists(Path.Combine(current.FullName, "GuideAntsApi.Tests")))
            {
                return current.FullName;
            }
        }
        throw new InvalidOperationException("Unable to locate server root.");
    }

    [GeneratedRegex(@"PendingDiskDelete|DeferredDelete|PendingDiskCleanup|ReplayPendingDeletes")]
    private static partial Regex DeferredCleanupRegex();
}
