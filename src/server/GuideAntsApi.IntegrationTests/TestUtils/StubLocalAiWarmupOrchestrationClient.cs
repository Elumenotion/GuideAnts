using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.IntegrationTests.TestUtils;

/// <summary>
/// In-process stand-in for <see cref="ILocalAiWarmupOrchestrationClient"/>.
/// Readiness / overview probes call <see cref="GetStatusAsync"/>; the real client
/// uses a 4-hour HttpClient against <c>localhost:8110</c> and hangs when the
/// upstream accepts TCP but never responds.
/// </summary>
public sealed class StubLocalAiWarmupOrchestrationClient : ILocalAiWarmupOrchestrationClient
{
    public Task<WarmupDesiredWriteResult> PutDesiredAsync(
        string iniText,
        int? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new WarmupDesiredWriteResult(
            Revision: expectedRevision ?? 1,
            Sha256: "stub",
            Changed: false));

    public Task<WarmupApplyResult> ApplyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WarmupApplyResult(
            Ok: true,
            Noop: true,
            Continue: false,
            Started: false,
            DesiredRevision: 0,
            AppliedRevision: 0,
            ApplyStatus: "idle"));

    public Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new WarmupStatusDocument(
            SchemaVersion: 1,
            DesiredRevision: 0,
            AppliedRevision: 0,
            InProgressRevision: null,
            ApplyStatus: "idle",
            ApplyError: null,
            DesiredSha256: string.Empty,
            WrittenAt: string.Empty,
            Services: new Dictionary<string, WarmupServiceStatus>()));
}
