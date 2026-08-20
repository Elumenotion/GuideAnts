using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiWarmupOrchestrationClientTests
{
    [TestMethod]
    public void MergeStackStatuses_IndependentRevisionCounters_UsesMaxAppliedRevision()
    {
        var stackOne = new WarmupStatusDocument(
            SchemaVersion: 1,
            DesiredRevision: 2,
            AppliedRevision: 2,
            InProgressRevision: null,
            ApplyStatus: "applied",
            ApplyError: null,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: new Dictionary<string, WarmupServiceStatus>());

        var stackTwo = new WarmupStatusDocument(
            SchemaVersion: 1,
            DesiredRevision: 1,
            AppliedRevision: 1,
            InProgressRevision: null,
            ApplyStatus: "applied",
            ApplyError: null,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: new Dictionary<string, WarmupServiceStatus>());

        var merged = LocalAiWarmupOrchestrationClient.MergeStackStatuses(
            [stackOne, stackTwo],
            new Dictionary<string, WarmupServiceStatus>());

        merged.DesiredRevision.Should().Be(2);
        merged.AppliedRevision.Should().Be(2);
        merged.ApplyStatus.Should().Be("applied");
        merged.DesiredRevision.Should().BeLessThanOrEqualTo(merged.AppliedRevision);
    }
}
