namespace GuideAntsApi.Services.Routing;

/// <summary>
/// How a readiness probe gathers runtime evidence.
///
/// <list type="bullet">
/// <item><see cref="Live"/> — the default. Probes engines directly (llama inventory,
/// local-engine <c>/ready</c>). Byte-for-byte what production callers get today.
/// Only use-path surfaces use it (dispatch preflight, explicit load/unload,
/// chat-readiness): a model is about to be used, so the guard must be live (R3).</item>
/// <item><see cref="NonBlocking"/> — issues <b>zero inline network calls</b>. Computes
/// configuration blockers from Plane A (DB) and fills runtime fields from the
/// Plane C cache synchronously. It does NOT mean "omit runtime state" (F-2): status
/// shown is status known, stamped with when it was observed.</item>
/// </list>
///
/// The separation itself is requirement-derived (S-1/S-2): R1 (instant render) and
/// R3 (live guards unchanged) cannot both hold on one handler. This spelling is
/// design; the split is not.
/// </summary>
public enum RoutingProbeMode
{
    /// <summary>Live engine probes. Default — existing production callers unchanged.</summary>
    Live = 0,

    /// <summary>No inline network call; runtime fields come from the Plane C cache.</summary>
    NonBlocking = 1,
}
