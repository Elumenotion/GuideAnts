namespace GuideAntsApi.Services.Routing;

/// <param name="Supported">False when the provider publishes no capability endpoint at all.</param>
/// <param name="Message">Human-readable explanation when values could not be determined.</param>
public sealed record ContextWindowProbeResult(
    bool Supported,
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    string? Message);

/// <summary>
/// Asks a provider what a model's context window is, where the provider publishes that.
/// Returns values for an admin to review — never writes to the catalog itself.
/// </summary>
public interface IModelContextWindowProbe
{
    Task<ContextWindowProbeResult> ProbeAsync(
        string modelId,
        string provider,
        CancellationToken cancellationToken);
}
