namespace GuideAntsApi.Services.Routing;

/// <summary>
/// A routing mode for a non-chat service (Embeddings, ImageGeneration, SpeechTranscription,
/// SpeechSynthesis, DocumentIntelligence). Chat routing is assistant-driven and is NOT
/// represented by modes.
/// </summary>
public sealed record ServiceMode(
    string ModeId,
    string ProviderSection,
    string? ModelId,
    string? RequestPresetJson,
    bool Enabled,
    bool IsDefault);

/// <summary>
/// Services that are routed via the mode matrix. Chat is deliberately excluded.
/// </summary>
public static class RoutedServiceNames
{
    public const string Embeddings = "Embeddings";
    public const string ImageGeneration = "ImageGeneration";
    public const string SpeechTranscription = "SpeechTranscription";
    public const string SpeechSynthesis = "SpeechSynthesis";
    public const string DocumentIntelligence = "DocumentIntelligence";

    public static readonly IReadOnlyList<string> All =
    [
        Embeddings,
        ImageGeneration,
        SpeechTranscription,
        SpeechSynthesis,
        DocumentIntelligence
    ];

    public static bool IsValid(string serviceName) =>
        All.Any(s => string.Equals(s, serviceName, StringComparison.OrdinalIgnoreCase));
}
