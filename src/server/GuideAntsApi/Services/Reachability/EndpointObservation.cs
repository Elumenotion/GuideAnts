namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// Three-valued endpoint availability. <see cref="Unknown"/> is a renderable,
/// first-class status ("not yet observed") — never a spinner, never an error
/// state. It is the truthful cold-cache state after an API restart (limit L-2):
/// engine down + fresh API means presence is Unknown, not a bug.
/// </summary>
public enum EndpointAvailability
{
    /// <summary>Never observed. Rendered as "not yet observed · Refresh".</summary>
    Unknown = 0,

    /// <summary>
    /// Any HTTP response was received — including 503. Per the availability
    /// classification rule, an up-but-idle engine (which answers 503 while
    /// not loaded) is Reachable, never Unreachable.
    /// </summary>
    Reachable = 1,

    /// <summary>
    /// Socket-level failure only: connection refused, DNS failure, or connect
    /// / response timeout. A non-2xx status is NOT Unreachable.
    /// </summary>
    Unreachable = 2,
}

/// <summary>
/// A single out-of-band observation of one local endpoint (Plane C). Produced by
/// the background probe loop and read synchronously by configuration surfaces.
/// This is observability data, never an instruction (D3/D4), and it may be stale
/// or absent — it is never awaited and never omitted when known (F-2).
/// </summary>
public sealed record EndpointObservation(
    string UrlKey,
    EndpointAvailability Availability,
    DateTime? ObservedUtc,
    int? LatencyMs,
    string? Error,
    int ConsecutiveFailures,
    string? RuntimeState = null)
{
    /// <summary>
    /// A never-observed sentinel: <see cref="EndpointAvailability.Unknown"/> with
    /// no timestamp and no error. A configuration surface renders this as
    /// "not yet observed · Refresh", never a spinner and never a blank.
    /// </summary>
    public static EndpointObservation Unknown(string urlKey) =>
        new(urlKey, EndpointAvailability.Unknown, null, null, null, 0, null);
}
