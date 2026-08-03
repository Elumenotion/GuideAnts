namespace GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

/// <summary>
/// The single definition of which <see cref="DataModel.Models.LocalModelOperation"/>
/// statuses are terminal.
/// </summary>
/// <remarks>
/// Two invariants depend on this being the only definition:
/// <list type="bullet">
/// <item>A non-terminal row blocks new operations on its alias
/// (see <c>LocalModelLifecycleService.EnsureNoInFlightOperationAsync</c>).</item>
/// <item>A non-terminal row is always selected by its owning reconciler sweep.</item>
/// </list>
/// If a sweep were to select an allow-list of in-flight statuses instead of the
/// complement of this set, any status outside both lists would block its alias
/// forever with nothing able to advance or fail it.
/// </remarks>
public static class LocalModelOperationStatuses
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> Terminal = [Completed, Failed];

    public static bool IsTerminal(string? status) =>
        string.Equals(status, Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase);
}
