using System.Text.Json;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Routing;

/// <inheritdoc />
public sealed class ModelContextWindowProbe : IModelContextWindowProbe
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderConfigurationResolver _providers;

    public ModelContextWindowProbe(
        IHttpClientFactory httpClientFactory,
        IProviderConfigurationResolver providers)
    {
        _httpClientFactory = httpClientFactory;
        _providers = providers;
    }

    public async Task<ContextWindowProbeResult> ProbeAsync(
        string modelId,
        string provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return provider?.ToLowerInvariant() switch
            {
                "anthropic" => await ProbeAnthropicAsync(modelId, cancellationToken),
                "openrouter-chat" => await ProbeOpenRouterAsync(modelId, cancellationToken),
                _ => new ContextWindowProbeResult(
                    false, null, null,
                    $"'{provider}' does not publish model context windows. Enter the value manually.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ContextWindowProbeResult(true, null, null, $"Probe failed: {ex.Message}");
        }
    }

    private async Task<ContextWindowProbeResult> ProbeAnthropicAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        var config = _providers.GetAnthropicConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return new ContextWindowProbeResult(
                true, null, null, "No Anthropic API key is configured.");
        }

        var baseUrl = (config.BaseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models/{Uri.EscapeDataString(modelId)}");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ContextWindowProbeResult(
                true, null, null, $"Anthropic returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return BuildCoherentResult(
            ReadInt(root, "max_input_tokens"),
            ReadInt(root, "max_tokens"));
    }

    private async Task<ContextWindowProbeResult> ProbeOpenRouterAsync(
        string modelId,
        CancellationToken cancellationToken)
    {
        var baseUrl = (_providers.GetOpenRouterOptions().BaseUrl ?? "https://openrouter.ai/api/v1")
            .TrimEnd('/');

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync($"{baseUrl}/models", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new ContextWindowProbeResult(
                true, null, null, $"OpenRouter returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return new ContextWindowProbeResult(
                true, null, null, "OpenRouter returned an unexpected response shape.");
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("id", out var id)
                || !string.Equals(id.GetString(), modelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The served window (top_provider) is what actually rejects requests; the
            // top-level context_length is the model's advertised maximum and can be larger.
            int? servedWindow = null;
            int? maxOutput = null;
            if (entry.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object)
            {
                servedWindow = ReadInt(top, "context_length");
                maxOutput = ReadInt(top, "max_completion_tokens");
            }

            return BuildCoherentResult(servedWindow ?? ReadInt(entry, "context_length"), maxOutput);
        }

        return new ContextWindowProbeResult(
            true, null, null, $"'{modelId}' is not listed in the OpenRouter catalog.");
    }

    // A wrong number misleads silently, so an output cap that is not below its window, or that
    // takes more than half of it (e.g. 512,000 of 524,288, leaving ~12k of prompt headroom), is
    // dropped rather than returned.
    private static ContextWindowProbeResult BuildCoherentResult(int? contextWindow, int? maxOutput)
    {
        if (contextWindow is { } window && maxOutput is { } output && (long)output * 2 > window)
        {
            return new ContextWindowProbeResult(
                true,
                window,
                null,
                "The provider's output cap was inconsistent with its context window; enter the max output manually.");
        }

        return new ContextWindowProbeResult(true, contextWindow, maxOutput, null);
    }

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
        && parsed > 0
            ? parsed
            : null;
}
