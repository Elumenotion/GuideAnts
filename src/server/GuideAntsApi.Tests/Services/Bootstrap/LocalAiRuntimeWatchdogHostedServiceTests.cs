using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiRuntimeWatchdogHostedServiceTests
{
    [TestMethod]
    [DataRow(0, "idle", true)]
    [DataRow(0, "applied", true)]
    [DataRow(3, "idle", true)]
    [DataRow(3, "applied", false)]
    [DataRow(3, "applying", false)]
    public void ExecutorNeedsApiPlan_DetectsContainerReset(
        int desiredRevision,
        string applyStatus,
        bool expected)
    {
        var status = new WarmupStatusDocument(
            SchemaVersion: 2,
            DesiredRevision: desiredRevision,
            AppliedRevision: desiredRevision,
            InProgressRevision: null,
            ApplyStatus: applyStatus,
            ApplyError: null,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: new Dictionary<string, WarmupServiceStatus>());

        LocalAiRuntimeWatchdogHostedService.ExecutorNeedsApiPlan(status).Should().Be(expected);
    }

    [TestMethod]
    public void ExecutorNeedsApiPlan_DesiredServiceNotApplied_NeedsPlan()
    {
        var status = new WarmupStatusDocument(
            SchemaVersion: 2,
            DesiredRevision: 4,
            AppliedRevision: 4,
            InProgressRevision: null,
            ApplyStatus: "applied",
            ApplyError: null,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: new Dictionary<string, WarmupServiceStatus>(StringComparer.Ordinal)
            {
                ["SpeechTranscription"] = new WarmupServiceStatus(
                    Desired: "on",
                    Applied: "off",
                    Phase: "idle",
                    Error: null,
                    PlanRef: "whisper-large",
                    RouterAlias: null,
                    ModelId: null,
                    BundleId: null),
            });

        LocalAiRuntimeWatchdogHostedService.ExecutorNeedsApiPlan(status).Should().BeTrue();
    }
}
