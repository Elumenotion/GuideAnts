namespace GuideAntsApi.Services.Bootstrap;

public sealed record WarmupDesiredWriteResult(
    int Revision,
    string Sha256,
    bool Changed);

public sealed record WarmupApplyResult(
    bool Ok,
    bool Noop,
    bool Continue,
    bool Started,
    int DesiredRevision,
    int AppliedRevision,
    string ApplyStatus);

public sealed record WarmupServiceStatus(
    string Desired,
    string Applied,
    string Phase,
    string? Error,
    string? PlanRef,
    string? RouterAlias,
    string? ModelId,
    string? BundleId);

public sealed record WarmupStatusDocument(
    int SchemaVersion,
    int DesiredRevision,
    int AppliedRevision,
    int? InProgressRevision,
    string ApplyStatus,
    string? ApplyError,
    string DesiredSha256,
    string WrittenAt,
    IReadOnlyDictionary<string, WarmupServiceStatus> Services);
