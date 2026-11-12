namespace GuideAntsApi.Services.Reachability;

/// <summary>
/// The out-of-band verdict for one local service after a probe cycle (Plane C).
/// Carried to <see cref="ILocalModelObservationRecorder"/> so the API-owned
/// install memory (Plane B) can record observed presence without ever being an
/// authority: <see cref="SelectedArtifactPresent"/> is a comparison the probe
/// loop performed, not an instruction.
/// </summary>
public sealed record LocalServiceObservation(
    string ServiceId,
    EndpointAvailability Availability,
    string? RuntimeState,
    bool? SelectedArtifactPresent,
    DateTime ObservedUtc,
    string? Error);

/// <summary>
/// Out-of-band sink for probe verdicts. Implemented by the API-owned install
/// memory (Plane B, P5): it stamps <c>ObservedState</c>/<c>ObservedUtc</c>/
/// <c>ObservedError</c> on the service's install row — never a <c>ServiceModes</c>
/// selection (D4; enforced by T-A2). The probe loop resolves this per cycle
/// through a DI scope; it is optional so the plane can run before Plane B exists.
/// </summary>
public interface ILocalModelObservationRecorder
{
    Task RecordServiceAsync(
        string serviceId,
        bool reachable,
        bool? selectedArtifactPresent,
        string? error,
        DateTime observedUtc,
        CancellationToken cancellationToken = default);
}
