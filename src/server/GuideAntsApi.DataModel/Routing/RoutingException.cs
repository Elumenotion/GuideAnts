namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Stable routing error codes surfaced over the wire (see R-2.1 in
/// docs/settings-and-llama-completion-requirements.md). These are consumed by the
/// client and MUST NOT be changed without a coordinated UI update.
/// </summary>
public static class RoutingErrorCodes
{
    public const string ModeNotFound = "ROUTING_MODE_NOT_FOUND";
    public const string ProviderNotReady = "ROUTING_PROVIDER_NOT_READY";
    public const string ModelNotReady = "ROUTING_MODEL_NOT_READY";
    public const string RuntimeNotReady = "ROUTING_RUNTIME_NOT_READY";
}

/// <summary>
/// Thrown by routing resolvers / validators when a chat or non-chat dispatch cannot
/// be fulfilled. Carries a stable machine-readable code plus structured context that
/// RoutingProblemDetailsFactory turns into an RFC 7807 problem+json.
/// <para>
/// Per R-1.5 chat MUST NOT emit <see cref="RoutingErrorCodes.ModeNotFound"/> — that
/// code is reserved for the five non-chat service modes.
/// </para>
/// </summary>
public sealed class RoutingException : Exception
{
    public string Code { get; }
    public string? ServiceId { get; }
    public string? ModeId { get; }
    public string? ProviderSection { get; }
    public string? ModelId { get; }

    /// <summary>
    /// Human-readable remediation hint (R-2.2). Intended for operator-facing UI.
    /// Examples: "Pick a provider section in the mode editor.",
    /// "Load the model from Settings → Models &amp; Runtime → Local Llama Runtime."
    /// </summary>
    public string Action { get; }

    public IReadOnlyList<string> Blockers { get; }

    public RoutingException(
        string code,
        string message,
        string action,
        string? serviceId = null,
        string? modeId = null,
        string? providerSection = null,
        string? modelId = null,
        IReadOnlyList<string>? blockers = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Action = action ?? throw new ArgumentNullException(nameof(action));
        ServiceId = serviceId;
        ModeId = modeId;
        ProviderSection = providerSection;
        ModelId = modelId;
        Blockers = blockers ?? Array.Empty<string>();
    }

    public static RoutingException ModeNotFound(string serviceId, string modeId) =>
        new(
            RoutingErrorCodes.ModeNotFound,
            $"Service '{serviceId}' has no mode '{modeId}' configured.",
            action: $"Add a '{modeId}' mode on the Routing tab under {serviceId}, or request a different mode id.",
            serviceId: serviceId,
            modeId: modeId);

    public static RoutingException ProviderNotReady(
        string providerSection,
        IReadOnlyList<string> blockers,
        string? serviceId = null,
        string? modeId = null) =>
        new(
            RoutingErrorCodes.ProviderNotReady,
            $"Provider section '{providerSection}' is not ready: {string.Join("; ", blockers)}",
            action: $"Open Settings → Connections → {providerSection} and fill in the missing fields.",
            serviceId: serviceId,
            modeId: modeId,
            providerSection: providerSection,
            blockers: blockers);

    public static RoutingException ModelNotReady(
        string modelId,
        string reason,
        string? serviceId = null,
        string? action = null) =>
        new(
            RoutingErrorCodes.ModelNotReady,
            $"Model '{modelId}' is not ready: {reason}",
            action: action ?? $"Open Settings → Models & Runtime and ensure '{modelId}' exists and is active.",
            serviceId: serviceId,
            modelId: modelId,
            blockers: new[] { reason });

    public static RoutingException RuntimeNotReady(
        string detail,
        string? modelId = null,
        string? action = null) =>
        new(
            RoutingErrorCodes.RuntimeNotReady,
            $"Local runtime is not ready: {detail}",
            action: action ?? "Open Settings → Models & Runtime → Local Llama Runtime and load the required alias.",
            modelId: modelId,
            blockers: new[] { detail });
}
