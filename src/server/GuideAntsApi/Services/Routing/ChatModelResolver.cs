using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Reads <c>ChatDefaults</c> from configuration (DB-backed application settings + reload).
/// </summary>
public sealed class ChatModelResolver : IChatModelResolver
{
    private const string SectionPrefix = "ChatDefaults:";
    private readonly IConfiguration _configuration;

    public ChatModelResolver(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public ResolvedChatModel Resolve(string? entityModelId)
    {
        var overrideAll = _configuration.GetValue<bool>($"{SectionPrefix}OverrideAllChatModels");
        var defaultId = (_configuration[$"{SectionPrefix}DefaultModelId"] ?? string.Empty).Trim();

        if (overrideAll)
        {
            if (string.IsNullOrWhiteSpace(defaultId))
            {
                throw RoutingException.ModelNotReady(
                    "(default)",
                    "Override all chat models is enabled but no default catalog model is configured.",
                    serviceId: "Chat",
                    action: "Open Settings → Overview (or Connections) and set Default Chat Model → Default model id.");
            }

            return new ResolvedChatModel(
                defaultId,
                ChatModelReferenceKind.OverriddenToDefault,
                ReadOptionalFloat($"{SectionPrefix}Temperature"),
                ReadOptionalFloat($"{SectionPrefix}TopP"),
                ReadOptionalString($"{SectionPrefix}ReasoningEffort"));
        }

        var trimmedEntity = (entityModelId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedEntity))
        {
            return new ResolvedChatModel(trimmedEntity, ChatModelReferenceKind.Direct, null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(defaultId))
        {
            return new ResolvedChatModel(
                defaultId,
                ChatModelReferenceKind.DefaultedTo,
                ReadOptionalFloat($"{SectionPrefix}Temperature"),
                ReadOptionalFloat($"{SectionPrefix}TopP"),
                ReadOptionalString($"{SectionPrefix}ReasoningEffort"));
        }

        throw RoutingException.ModelNotReady(
            "(unset)",
            "No chat model is configured on this assistant and no global default is set.",
            serviceId: "Chat",
            action: "Pick a model in Guide Builder, or configure Settings → Overview → Default Chat Model.");
    }

    private float? ReadOptionalFloat(string key)
    {
        var raw = _configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    private string? ReadOptionalString(string key)
    {
        var raw = _configuration[key];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
