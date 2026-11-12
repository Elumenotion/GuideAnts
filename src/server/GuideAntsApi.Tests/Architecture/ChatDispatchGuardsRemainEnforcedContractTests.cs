using FluentAssertions;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Tests.Architecture;

/// <summary>T-A3 — deleting or softening a guard fails the build. The use-path guards
/// survive the three-plane split untouched; the RUNTIME_STATE blocker text is matched by
/// substring (not line range, which would pass vacuously — the plan's correction).</summary>
[TestClass]
public sealed class ChatDispatchGuardsRemainEnforcedContractTests
{
    [TestMethod]
    public void DispatchPreflight_StillGuardsOnLiveRuntimeStatus()
    {
        var path = Path.Combine(FindServerRoot(), "GuideAntsApi", "Endpoints", "NotebookConversationsEndpoints.cs");
        var text = File.ReadAllText(path);

        text.Should().Contain("GetRuntimeStatusAsync",
            "the 409 preflight still consults live per-instance llama state");
        text.Should().Contain("Results.Conflict",
            "the 409 preflight still returns a conflict");
    }

    [TestMethod]
    public void ChatReadiness_StillCallsProbeChatTarget_InLiveMode()
    {
        var path = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "NotebookHeaderToolbar", "NotebookHeaderToolbarService.cs");
        var text = File.ReadAllText(path);

        // GetChatReadinessAsync keeps its live probe (R3). The BuildChatSectionAsync call
        // site for the readiness path threads RoutingProbeMode.Live explicitly.
        text.Should().Contain("RoutingProbeMode.Live",
            "the chat-readiness use path names Live so a cached read cannot be substituted");
    }

    [TestMethod]
    public void LoadGuard_StillAwaitsReadinessVerification()
    {
        var path = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "Bootstrap", "LocalServiceLoadService.cs");
        var text = File.ReadAllText(path);

        text.Should().Contain("WaitForLoadedAsync",
            "LoadServiceAsync still awaits readiness through the full verify budget");
        text.Should().Contain("ReadyTimeout",
            "the load verify budget remains the 3-minute ReadyTimeout, not the probe budget");
    }

    [TestMethod]
    public void RuntimeStateBlockerText_IsStillEmitted_MatchedBySubstring()
    {
        var path = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "Routing", "RoutingReadinessService.cs");
        var text = File.ReadAllText(path);

        // Substring match on the message core (line ranges pass vacuously when code moves).
        text.Should().Contain("local engine not loaded for",
            "the RUNTIME_STATE blocker text is load-bearing for toolbar status mapping");
    }

    [TestMethod]
    public void F4Boundary_EndpointUnreachable_IsNeverEmittedOnTheLlamaBranch()
    {
        var path = Path.Combine(FindServerRoot(), "GuideAntsApi", "Services", "Routing", "RoutingReadinessService.cs");
        var text = File.ReadAllText(path);

        // ENDPOINT_UNREACHABLE is emitted only from the aux branch (BuildAuxRuntimeBlockers-
        // FromCache). The llama/chat branch (ProbeLlamaRuntimeAsync) must not reference it.
        var llamaStart = text.IndexOf("private async Task<(string? RuntimeState, IReadOnlyList<string> Blockers)> ProbeLlamaRuntimeAsync(", StringComparison.Ordinal);
        llamaStart.Should().BeGreaterThan(0, "the llama branch exists");
        var llamaEnd = text.IndexOf("\n    private ", llamaStart + 10, StringComparison.Ordinal);
        if (llamaEnd < 0) llamaEnd = text.Length;
        var llamaBody = text[llamaStart..llamaEnd];

        llamaBody.Should().NotContain("EndpointUnreachable",
            "a non-whitelisted blocker on a chat row would hide it from the selectable list (F-4)");
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
}
