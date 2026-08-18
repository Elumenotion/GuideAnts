namespace GuideAntsApi.Services.Bootstrap;

public sealed record WarmupApplyResult(
    bool Ok,
    bool Noop,
    bool Continue,
    bool Started,
    int DesiredRevision,
    int AppliedRevision,
    string ApplyStatus,
    bool Changed);

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
