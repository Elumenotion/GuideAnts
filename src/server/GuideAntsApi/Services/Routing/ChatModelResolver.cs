using System.Text.Json;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// Reads <c>ChatDefaults</c> from configuration (DB-backed application settings + reload).
/// </summary>
public sealed class ChatModelResolver : IChatModelResolver
{
    private const string SectionPrefix = "ChatDefaults:";
    private readonly IConfiguration _configuration;
    private readonly IChatTargetResolver _chatTargetResolver;

    public ChatModelResolver(IConfiguration configuration, IChatTargetResolver chatTargetResolver)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _chatTargetResolver = chatTargetResolver ?? throw new ArgumentNullException(nameof(chatTargetResolver));
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
                    action: "Open Settings \u2192 Overview (or Connections) and set Default Chat Model \u2192 Default model id.");
            }

            return BuildResolvedModel(
                defaultId,
                ChatModelReferenceKind.OverriddenToDefault,
                ParameterAuthority.GlobalOverride,
                BuildDefaultParameters(defaultId));
        }

        var trimmedEntity = (entityModelId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedEntity))
        {
            return BuildResolvedModel(
                trimmedEntity,
                ChatModelReferenceKind.Direct,
                ParameterAuthority.AssistantDefinition,
                EmptyParameters);
        }

        if (!string.IsNullOrWhiteSpace(defaultId))
        {
            return BuildResolvedModel(
                defaultId,
                ChatModelReferenceKind.DefaultedTo,
                ParameterAuthority.AssistantDefinition,
                BuildDefaultParameters(defaultId));
        }

        throw RoutingException.ModelNotReady(
            "(unset)",
            "No chat model is configured on this assistant and no global default is set.",
            serviceId: "Chat",
            action: "Pick a model in Guide Builder, or configure Settings \u2192 Overview \u2192 Default Chat Model.");
    }

    private ResolvedChatModel BuildResolvedModel(
        string modelId,
        ChatModelReferenceKind referenceKind,
        ParameterAuthority authority,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var target = _chatTargetResolver.Resolve(modelId);
        var policy = new ResolvedExecutionPolicy(modelId, target.Provider, authority, parameters);
        return new ResolvedChatModel(modelId, referenceKind, policy);
    }

    private IReadOnlyDictionary<string, JsonElement> BuildDefaultParameters(string modelId)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var temperature = ReadOptionalFloat($"{SectionPrefix}Temperature");
        if (temperature.HasValue)
        {
            result["temperature"] = JsonSerializer.SerializeToElement((double)temperature.Value);
        }

        var topP = ReadOptionalFloat($"{SectionPrefix}TopP");
        if (topP.HasValue)
        {
            result["top_p"] = JsonSerializer.SerializeToElement((double)topP.Value);
        }

        var reasoningEffort = ReadOptionalString($"{SectionPrefix}ReasoningEffort");
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            result["reasoning_effort"] = JsonSerializer.SerializeToElement(reasoningEffort);
        }

        var samplingJson = _configuration[$"{SectionPrefix}SamplingParametersJson"];
        if (string.IsNullOrWhiteSpace(samplingJson))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(samplingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw RoutingException.ModelNotReady(
                    modelId,
                    "ChatDefaults:SamplingParametersJson must be a JSON object.",
                    serviceId: "Chat",
                    action: "Open Settings \u2192 Overview \u2192 Default Chat Model and provide a valid sampling JSON object.");
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException ex)
        {
            throw RoutingException.ModelNotReady(
                modelId,
                $"ChatDefaults:SamplingParametersJson is invalid JSON: {ex.Message}",
                serviceId: "Chat",
                action: "Open Settings \u2192 Overview \u2192 Default Chat Model and fix SamplingParametersJson.");
        }

        return result;
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

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyParameters =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
