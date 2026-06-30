using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.PublishedWireApi;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireModelAliasResolver
{
internal static class AliasKeys
    {
        public const string Guide = "guide";
        public const string Embeddings = "embeddings";
        public const string Image = "image";
        public const string Transcription = "transcription";
        public const string Speech = "speech";
    }

internal static (string Alias, IResult? ErrorResult) ResolveModelAliasOrError(
    PublishedApiExecutionContext context,
    string aliasKey,
    string? requestedModel)
{
    var configuredAlias = ResolveConfiguredAlias(context.WireApiConfig, aliasKey);
    if (string.IsNullOrWhiteSpace(requestedModel))
    {
        return (configuredAlias, null);
    }

    if (string.Equals(configuredAlias, requestedModel, StringComparison.OrdinalIgnoreCase))
    {
        return (configuredAlias, null);
    }

    return (configuredAlias, OpenAiWireErrorResults.MissingModelAlias(requestedModel));
}

internal static IReadOnlyList<string> BuildEnabledModelAliases(PublishedWireApiConfigDto config)
{
    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var flags = config.EndpointFlags ?? new PublishedWireApiEndpointFlagsDto();

    if (flags.ChatCompletions != false || flags.Responses != false || flags.Messages != false)
    {
        aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Guide));
    }
    if (flags.Embeddings != false)
    {
        aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Embeddings));
    }
    if (flags.ImageGenerations != false)
    {
        aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Image));
    }
    if (flags.AudioTranscriptions != false)
    {
        aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Transcription));
    }
    if (flags.AudioSpeech != false)
    {
        aliases.Add(ResolveConfiguredAlias(config, AliasKeys.Speech));
    }

    return aliases.OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase).ToArray();
}

internal static string ResolveConfiguredAlias(PublishedWireApiConfigDto config, string aliasKey)
{
    var alias = aliasKey;
    if (config.AliasMap != null &&
        config.AliasMap.TryGetValue(aliasKey, out var configured) &&
        !string.IsNullOrWhiteSpace(configured))
    {
        alias = configured.Trim();
    }

    return alias;
}
}
