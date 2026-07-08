using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.SandboxWireApi;

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
    string? requestedModel) =>
    ResolveModelAliasOrError(new PublishedWireExecutionContextAdapter(context), aliasKey, requestedModel);

internal static (string Alias, IResult? ErrorResult) ResolveModelAliasOrError(
    IWireExecutionContext context,
    string aliasKey,
    string? requestedModel)
{
    var configuredAlias = ResolveConfiguredAlias(context.AliasMap, aliasKey);
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

internal static IReadOnlyList<string> BuildEnabledModelAliases(IWireExecutionContext context)
{
    var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var flags = context.EndpointFlags ?? new PublishedWireApiEndpointFlagsDto();

    if (flags.ChatCompletions != false || flags.Responses != false || flags.Messages != false)
    {
        aliases.Add(ResolveConfiguredAlias(context.AliasMap, AliasKeys.Guide));
    }
    if (flags.Embeddings != false)
    {
        aliases.Add(ResolveConfiguredAlias(context.AliasMap, AliasKeys.Embeddings));
    }
    if (flags.ImageGenerations != false)
    {
        aliases.Add(ResolveConfiguredAlias(context.AliasMap, AliasKeys.Image));
    }
    if (flags.AudioTranscriptions != false)
    {
        aliases.Add(ResolveConfiguredAlias(context.AliasMap, AliasKeys.Transcription));
    }
    if (flags.AudioSpeech != false)
    {
        aliases.Add(ResolveConfiguredAlias(context.AliasMap, AliasKeys.Speech));
    }

    return aliases.OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase).ToArray();
}

internal static IReadOnlyList<string> BuildEnabledModelAliases(PublishedWireApiConfigDto config)
{
    var flags = config.EndpointFlags ?? new PublishedWireApiEndpointFlagsDto();
    return BuildEnabledModelAliases(new PublishedWireApiConfigWireContextAdapter(config));
}

internal static string ResolveConfiguredAlias(Dictionary<string, string>? aliasMap, string aliasKey)
{
    var alias = aliasKey;
    if (aliasMap != null &&
        aliasMap.TryGetValue(aliasKey, out var configured) &&
        !string.IsNullOrWhiteSpace(configured))
    {
        alias = configured.Trim();
    }

    return alias;
}

internal static string ResolveConfiguredAlias(PublishedWireApiConfigDto config, string aliasKey) =>
    ResolveConfiguredAlias(config.AliasMap, aliasKey);

private sealed class PublishedWireApiConfigWireContextAdapter(PublishedWireApiConfigDto config) : IWireExecutionContext
{
    public Guid ProjectId => Guid.Empty;
    public Guid NotebookId => Guid.Empty;
    public Guid OwnerAssistantId => Guid.Empty;
    public Guid TargetAssistantId => Guid.Empty;
    public string TargetAssistantName => string.Empty;
    public string PublisherId => string.Empty;
    public string? ExternalUserIdentity => null;
    public Guid? InternalUserId => null;
    public Guid? AttributionConversationId => null;
    public string SourceChannel => string.Empty;
    public string ExternalRequestId => string.Empty;
    public string EndpointName => string.Empty;
    public PublishedWireApiEndpointFlagsDto? EndpointFlags => config.EndpointFlags;
    public Dictionary<string, string>? AliasMap => config.AliasMap;
    public PublishedWireApiMaxRequestSizesDto? MaxRequestSizes => config.MaxRequestSizes;
}
}
