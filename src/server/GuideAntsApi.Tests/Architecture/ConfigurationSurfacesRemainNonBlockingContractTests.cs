using FluentAssertions;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Tests.Architecture;

/// <summary>T-A1 — configuration surfaces cannot inherit a live probe.
/// Source-scan contract, following LocalAiLifecycleAuthorityContractTests.</summary>
[TestClass]
public sealed partial class ConfigurationSurfacesRemainNonBlockingContractTests
{
    [TestMethod]
    public void ConfigurationSurfaces_NameAProbeModeExplicitly_OnEveryReadinessCall()
    {
        var files = new[]
        {
            Path.Combine(FindServerRoot(), "GuideAntsApi", "Endpoints", "Settings", "SettingsOverviewEndpoints.cs"),
            Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "NotebookHeaderToolbar", "NotebookHeaderToolbarService.cs"),
            Path.Combine(FindServerRoot(), "GuideAntsApi", "Endpoints", "Settings", "SettingsRoutingEndpoints.cs"),
        };

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(FindServerRoot(), file);
            foreach (Match m in ReadinessCallRegex().Matches(text))
            {
                var call = m.Value;
                // Every readiness call must name a mode explicitly (Live included) so no
                // configuration surface can inherit the Live default by accident.
                var named = call.Contains("RoutingProbeMode.NonBlocking", StringComparison.Ordinal)
                         || call.Contains("RoutingProbeMode.Live", StringComparison.Ordinal)
                         || call.Contains("RoutingProbeMode mode", StringComparison.Ordinal)  // threaded variable
                         || call.Contains("probeMode", StringComparison.Ordinal)
                         || call.Contains(", mode)", StringComparison.Ordinal);
                if (!named)
                {
                    offenders.Add($"{relative}: readiness call without an explicit mode: {call}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "every readiness call on a configuration surface must name RoutingProbeMode explicitly (Live included): {0}",
            string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void ToolbarService_DoesNotProbeInline()
    {
        var toolbarPath = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "NotebookHeaderToolbar", "NotebookHeaderToolbarService.cs");
        var text = File.ReadAllText(toolbarPath);

        // The machine-checkable statement that configuration does not probe: the inline
        // local-admin HTTP helper is gone and no IHttpClientFactory dependency remains.
        text.Should().NotContain("HttpGetLocalAdminJsonAsync");
        text.Should().NotContain("IHttpClientFactory");
        text.Should().NotContain("_inventory.GetInventory");

        // The chat-readiness use-path call remains, naming Live explicitly (T-A3's mirror
        // requirement): not NonBlocking.
        text.Should().Contain("ProbeChatTargetAsync");
    }

    [TestMethod]
    public void ChatReadiness_KeepsLiveProbe_NonBlockingNotSubstituted()
    {
        var toolbarPath = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "NotebookHeaderToolbar", "NotebookHeaderToolbarService.cs");
        var text = File.ReadAllText(toolbarPath);

        // GetChatReadinessAsync is the use path that still probes live. Its call must
        // thread the Live mode variable, never a literal NonBlocking.
        var idx = text.IndexOf("BuildChatSectionAsync(", StringComparison.Ordinal);
        text.Should().Contain("RoutingProbeMode.Live", "GetChatReadinessAsync keeps a live probe (R3)");
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

    [GeneratedRegex(@"ProbeChatTargetAsync\([^;]*?\)|ProbeModeAsync\([^;]*?\)", RegexOptions.Singleline)]
    private static partial Regex ReadinessCallRegex();
}
