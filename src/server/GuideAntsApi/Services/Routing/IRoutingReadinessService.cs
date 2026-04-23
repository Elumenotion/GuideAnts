using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Phase B (R-5.2 / R-7.1 / R-9.2) readiness composition. Combines provider
/// section configuration, catalog row state, and local llama runtime inventory
/// into a single <see cref="ModeReadinessDto"/> / <see cref="ChatTargetReadinessDto"/>
/// suitable for the Settings Overview and preflight endpoints.
///
/// Fail-fast contract: any missing prerequisite is surfaced as a blocker string;
/// no component is silently substituted (user rule — no fallback).
/// </summary>
public interface IRoutingReadinessService
{
    Task<ModeReadinessDto> ProbeModeAsync(string service, string modeId, CancellationToken cancellationToken = default);

    /// <param name="modelId">Catalog model id to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="referenceKind">Per R-9.2: <c>direct</c>, <c>defaultedTo</c>, or <c>overriddenToDefault</c>.</param>
    Task<ChatTargetReadinessDto> ProbeChatTargetAsync(
        string modelId,
        CancellationToken cancellationToken = default,
        string referenceKind = "direct");
}
