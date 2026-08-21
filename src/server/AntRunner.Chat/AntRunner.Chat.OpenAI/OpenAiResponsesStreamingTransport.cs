using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AntRunner.Chat.OpenAI;

/// <summary>
/// Sends stateless Responses API requests and treats the terminal SSE event as authoritative.
/// GuideAnts owns conversation state, so this transport never retrieves or chains provider responses.
/// </summary>
internal sealed class OpenAiResponsesStreamingTransport
{
    private readonly HttpClient _httpClient;
    private readonly AzureOpenAiConfig _config;

    public OpenAiResponsesStreamingTransport(HttpClient httpClient, AzureOpenAiConfig config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<JsonElement> CreateAsync(
        JsonObject request,
        Func<string, JsonElement, Task>? eventHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, BuildResponsesUri());
        AddAuthentication(requestMessage);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        requestMessage.Content = CreateRequestContent(request);

        using var response = await _httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Responses API POST failed with HTTP {(int)response.StatusCode} ({response.StatusCode}). " +
                $"Response body: {responseBody}",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(responseStream);

        string? eventName = null;
        var data = new StringBuilder();
        JsonElement? terminalResponse = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                terminalResponse = await ProcessEventAsync(
                    eventName,
                    data,
                    terminalResponse,
                    eventHandler).ConfigureAwait(false);
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].TrimStart();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line["data:".Length..].TrimStart());
            }
        }

        if (data.Length > 0)
        {
            terminalResponse = await ProcessEventAsync(
                eventName,
                data,
                terminalResponse,
                eventHandler).ConfigureAwait(false);
        }

        return terminalResponse
            ?? throw new InvalidOperationException(
                "Responses API stream ended without a terminal response.completed event.");
    }

    private static StringContent CreateRequestContent(JsonObject request)
    {
        var payloadObject = JsonNode.Parse(request.ToJsonString())?.AsObject()
            ?? throw new InvalidOperationException("Unable to clone the Responses API request.");

        // GuideAnts replays its own SQL transcript. Provider-side response persistence and chaining
        // are deliberately disabled, while streaming is required for live progress and tool calls.
        payloadObject["store"] = false;
        payloadObject["stream"] = true;
        payloadObject.Remove("previous_response_id");

        return new StringContent(
            payloadObject.ToJsonString(),
            Encoding.UTF8,
            new MediaTypeHeaderValue("application/json"));
    }

    private Uri BuildResponsesUri()
    {
        if (string.IsNullOrWhiteSpace(_config.ResourceName))
        {
            return new Uri("https://api.openai.com/v1/responses", UriKind.Absolute);
        }

        if (string.IsNullOrWhiteSpace(_config.ApiVersion))
        {
            throw new InvalidOperationException(
                "Azure OpenAI Responses configuration requires an API version.");
        }

        var resourceName = _config.ResourceName.Trim();
        if (resourceName.Contains('/') || resourceName.Contains('\\'))
        {
            throw new InvalidOperationException(
                "Azure OpenAI ResourceName must be the resource name, not a URL.");
        }

        var apiVersion = Uri.EscapeDataString(_config.ApiVersion.Trim());
        return new Uri(
            $"https://{resourceName}.openai.azure.com/openai/responses?api-version={apiVersion}",
            UriKind.Absolute);
    }

    private void AddAuthentication(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            throw new InvalidOperationException("OpenAI Responses configuration requires an API key.");
        }

        if (string.IsNullOrWhiteSpace(_config.ResourceName))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
            return;
        }

        request.Headers.TryAddWithoutValidation("api-key", _config.ApiKey);
    }

    private static async Task<JsonElement?> ProcessEventAsync(
        string? eventName,
        StringBuilder data,
        JsonElement? terminalResponse,
        Func<string, JsonElement, Task>? eventHandler)
    {
        if (data.Length == 0 || string.Equals(data.ToString(), "[DONE]", StringComparison.Ordinal))
        {
            return terminalResponse;
        }

        using var document = JsonDocument.Parse(data.ToString());
        var eventData = document.RootElement;
        var resolvedEventName = !string.IsNullOrWhiteSpace(eventName)
            ? eventName
            : ReadOptionalString(eventData, "type");

        if (string.IsNullOrWhiteSpace(resolvedEventName))
        {
            throw new InvalidOperationException("Responses API SSE event did not include an event type.");
        }

        if (eventHandler != null)
        {
            await eventHandler(resolvedEventName, eventData).ConfigureAwait(false);
        }

        switch (resolvedEventName)
        {
            case "response.completed":
                return ReadRequiredResponse(eventData).Clone();

            case "response.failed":
            case "response.incomplete":
                throw new InvalidOperationException(
                    $"Responses API returned terminal event '{resolvedEventName}': {eventData.GetRawText()}");

            case "error":
                throw new InvalidOperationException(
                    $"Responses API stream returned an error event: {eventData.GetRawText()}");

            default:
                return terminalResponse;
        }
    }

    private static JsonElement ReadRequiredResponse(JsonElement eventData)
    {
        if (eventData.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object)
        {
            return response;
        }

        throw new InvalidOperationException(
            "Responses API terminal event did not contain a response object.");
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
