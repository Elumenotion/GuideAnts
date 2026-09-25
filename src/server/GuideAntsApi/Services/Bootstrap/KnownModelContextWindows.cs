namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Context-window values for well-known hosted models, used to back-fill catalog rows that
/// predate the context-window columns. A model absent from this table keeps a null window and
/// renders as "unknown" — which is correct. Never add a value that has not been verified
/// against the provider's own documentation.
///
/// These values are duplicated in the client seed at
/// src/client/src/pages/settings/data/knownCloudModels.json. Keep the two identical; a test
/// (ModelContextWindowBackfillTests.MatchesClientSeedFile_ForEveryEntry) enforces parity.
/// gpt-4.1 family and minimax/minimax-m3 are deliberately absent (sources disagree).
/// </summary>
internal static class KnownModelContextWindows
{
    private static readonly Dictionary<string, (int ContextWindow, int? MaxOutput)> Values =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o"] = (128_000, 16_384),
            ["gpt-4o-mini"] = (128_000, 16_384),
            ["gpt-5-chat"] = (128_000, 16_384),
            ["gpt-5.1"] = (400_000, 128_000),
            ["o3"] = (200_000, 100_000),
            ["o4-mini"] = (200_000, 100_000),
            ["gpt-5"] = (400_000, 128_000),
            ["gpt-5-mini"] = (400_000, 128_000),
            ["gpt-5-nano"] = (400_000, 128_000),
            ["gpt-5.2-codex"] = (400_000, 128_000),
            ["claude-opus-4-5"] = (200_000, 64_000),
            ["claude-sonnet-4-5"] = (200_000, 64_000),
            ["claude-haiku-4-5"] = (200_000, 64_000),
            ["gemini-2.5-pro"] = (1_048_576, 65_536),
            ["gemini-2.5-flash"] = (1_048_576, 65_536),
        };

    internal static bool TryGet(string modelId, out int contextWindow, out int? maxOutput)
    {
        if (!string.IsNullOrWhiteSpace(modelId) && Values.TryGetValue(modelId, out var found))
        {
            contextWindow = found.ContextWindow;
            maxOutput = found.MaxOutput;
            return true;
        }

        contextWindow = 0;
        maxOutput = null;
        return false;
    }
}
