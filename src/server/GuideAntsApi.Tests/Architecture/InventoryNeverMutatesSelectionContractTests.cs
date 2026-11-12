using FluentAssertions;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Tests.Architecture;

/// <summary>T-A2 — makes D4 structural: no code path reachable from an inventory//ready/health
/// read may write a ServiceModes selection. Plane C must never become a second authority.</summary>
[TestClass]
public sealed partial class InventoryNeverMutatesSelectionContractTests
{
    [TestMethod]
    public void ReachabilityPlane_DoesNotWriteServiceModesSelections()
    {
        var reachabilityDir = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "Reachability");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(reachabilityDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(FindServerRoot(), file);

            // The selection authority lives in IApplicationSettingsService; the reachability
            // plane may read nothing that writes it. SetServiceModeModelIdAsync /
            // ClearServiceModeModelIdAsync must not appear in this plane at all.
            if (SelectionWriteRegex().IsMatch(text))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty(
            "no file under Services/Reachability may write a ServiceModes selection (D4): {0}",
            string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void ObservationRecorder_NeverTouchesSettingsService()
    {
        var recorderPath = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "Bootstrap", "LocalModelObservationRecorder.cs");
        var text = File.ReadAllText(recorderPath);

        // The Plane C→B bridge stamps observation columns only; it must not take or use
        // the selection authority.
        text.Should().NotContain("IApplicationSettingsService");
        text.Should().NotContain("SetServiceModeModelIdAsync");
        text.Should().NotContain("ClearServiceModeModelIdAsync");
    }

    [TestMethod]
    public void Store_MarkObserved_DoesNotMutateLifecycleState()
    {
        var storePath = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "Bootstrap", "ServiceLocalModelInstallationStore.cs");
        var text = File.ReadAllText(storePath);

        // MarkObservedAsync's body must only stamp observation columns. Source-level guard:
        // inside the MarkObservedAsync method there is no State assignment and no selection write.
        var start = text.IndexOf("public async Task MarkObservedAsync(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "MarkObservedAsync exists");
        var end = text.IndexOf("\n    public ", start + 10, StringComparison.Ordinal);
        if (end < 0) end = text.Length;
        var body = text[start..end];

        body.Should().NotContain("row.State =", "observation never mutates the API-owned lifecycle state");
        body.Should().NotContain("ServiceModes", "observation never touches selections");
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

    [GeneratedRegex(@"SetServiceModeModelIdAsync|ClearServiceModeModelIdAsync")]
    private static partial Regex SelectionWriteRegex();
}
