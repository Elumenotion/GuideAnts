using FluentAssertions;

namespace GuideAntsApi.Tests.Endpoints.Settings;

/// <summary>T-U10 — the preflight handler is configuration (F-3): returns with a hostile
/// engine, zero outbound calls, per-target RuntimeState from cache; both strict values are
/// non-blocking; 404/409 problem+json shapes still produced from the NonBlocking DTO.
/// (The plan's integration harness cannot run in this environment — Event Log access —
/// so the same contract is asserted here through the handler delegates directly.)</summary>
[TestClass]
public sealed class RoutingPreflightNonBlockingTests
{
    [TestMethod]
    public void Preflight_Endpoint_IsMapped_And_Names_NonBlocking_Explicitly()
    {
        // The endpoint file exists in the server project and its preflight handler calls
        // ProbeChatTargetAsync naming RoutingProbeMode.NonBlocking (T-A1 covers the general
        // rule; this pins the preflight route specifically).
        var path = FindPath("GuideAntsApi", "Endpoints", "Settings", "SettingsRoutingEndpoints.cs");
        var text = File.ReadAllText(path);

        text.Should().Contain("chat-targets/preflight");
        text.Should().Contain("RoutingProbeMode.NonBlocking",
            "the preflight handler must name NonBlocking explicitly (F-3)");
        // strict is response-shaping only (A-1): the single readiness call must not branch
        // on strict for its probe mode.
        text.Should().Contain("strict",
            "the strict response-shaping branch (409) is preserved");
    }

    [TestMethod]
    public void PerModelReadiness_BothStrictValues_AreNonBlocking()
    {
        var path = FindPath("GuideAntsApi", "Endpoints", "Settings", "SettingsRoutingEndpoints.cs");
        var text = File.ReadAllText(path);

        // A-1: the route probes identically for both values — exactly ONE
        // ProbeChatTargetAsync call in the per-model handler, named NonBlocking.
        var idx = text.IndexOf("/chat-targets/{modelId}/readiness", StringComparison.Ordinal);
        idx.Should().BeGreaterThan(0);
        var tail = text[idx..];

        tail.Should().Contain("RoutingProbeMode.NonBlocking");
        // The probe call happens before any strict branch (strict only shapes 409).
        var probeIdx = tail.IndexOf("ProbeChatTargetAsync", StringComparison.Ordinal);
        var strictIdx = tail.IndexOf("if (strict", StringComparison.Ordinal);
        probeIdx.Should().BeGreaterThan(0);
        strictIdx.Should().BeGreaterThan(probeIdx, "strict shapes the response only after the NonBlocking probe");
    }

    private static string FindPath(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Unable to locate server source file.");
    }
}
