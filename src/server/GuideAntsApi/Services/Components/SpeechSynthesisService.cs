using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using AntRunner.Chat.OpenRouter;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Concrete implementation of <see cref="ISpeechSynthesisService"/> supporting both Azure Speech and local TTS provider routing.
/// </summary>
public sealed class SpeechSynthesisService : ISpeechSynthesisService
{
    private sealed record HfProviderRoute(string Provider, string ProviderId);

    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechSynthesisBaseUrl";
    private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";
    private const string OpenAiProviderSection = "OpenAI";
    private static readonly JsonSerializerOptions ProviderPayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Regex SsmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AzureSpeechServiceOptions> _azureOptionsMonitor;
    private readonly IOptionsMonitor<SpeechSynthesisOptions> _synthesisOptionsMonitor;
    private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpeechSynthesisService> _logger;

    public SpeechSynthesisService(
        HttpClient httpClient,
        IOptionsMonitor<AzureSpeechServiceOptions> azureOptionsMonitor,
        IOptionsMonitor<SpeechSynthesisOptions> synthesisOptionsMonitor,
        IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptionsMonitor,
        IServiceModeResolver serviceModeResolver,
        IConfiguration configuration,
        ILogger<SpeechSynthesisService> logger)
    {
        _httpClient = httpClient;
        _azureOptionsMonitor = azureOptionsMonitor;
        _synthesisOptionsMonitor = synthesisOptionsMonitor;
        _localServiceHostsOptionsMonitor = localServiceHostsOptionsMonitor;
        _serviceModeResolver = serviceModeResolver;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeToWavAsync(
        string ssml,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ssml))
        {
            throw new ArgumentException("SSML may not be empty", nameof(ssml));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var mode = await _serviceModeResolver
            .ResolveAsync(RoutedServiceNames.SpeechSynthesis, modeId: null, cancellationToken)
            .ConfigureAwait(false);
        var requestId = Guid.NewGuid().ToString("N");

        var providerId = ResolveSpeechSynthesisProviderId(mode.ProviderSection);
        var result = mode.ProviderSection switch
        {
            LocalProviderSection => await SynthesizeViaLocalTtsAsync(ssml, outputPath, requestId, mode, cancellationToken),
            AzureProviderSection => await SynthesizeViaAzureAsync(ssml, outputPath, requestId, cancellationToken),
            GoogleGeminiProviderSection => await SynthesizeViaGoogleAsync(ssml, outputPath, requestId, mode, cancellationToken),
            HuggingFaceProviderSection => await SynthesizeViaHuggingFaceAsync(ssml, outputPath, requestId, mode, cancellationToken),
            OpenRouterProviderSection => await SynthesizeViaOpenRouterAsync(ssml, outputPath, requestId, mode, cancellationToken),
            OpenAiProviderSection => await SynthesizeViaOpenAiAsync(ssml, outputPath, requestId, mode, cancellationToken),
            _ => throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[]
                {
                    $"SpeechSynthesis mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                    $"Expected '{AzureProviderSection}', '{LocalProviderSection}', '{GoogleGeminiProviderSection}', '{HuggingFaceProviderSection}', '{OpenRouterProviderSection}', or '{OpenAiProviderSection}'."
                },
                serviceId: RoutedServiceNames.SpeechSynthesis,
                modeId: mode.ModeId)
        };

        return result with { ProviderId = providerId };
    }

    private static string ResolveSpeechSynthesisProviderId(string providerSection) => providerSection switch
    {
        AzureProviderSection => ServiceProviderIds.SpeechSynthesisAzureSpeechSsml,
        LocalProviderSection => ServiceProviderIds.SpeechSynthesisLocalTtsHttp,
        GoogleGeminiProviderSection => ServiceProviderIds.SpeechSynthesisGoogleTextToSpeech,
        HuggingFaceProviderSection => ServiceProviderIds.SpeechSynthesisHuggingFaceInference,
        OpenRouterProviderSection => ServiceProviderIds.SpeechSynthesisOpenRouterTts,
        OpenAiProviderSection => ServiceProviderIds.SpeechSynthesisOpenAiTts,
        _ => providerSection
    };

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaGoogleAsync(
        string ssml,
        string outputPath,
        string requestId,
        ServiceMode mode,
        CancellationToken cancellationToken)
    {
        var text = StripSsmlMarkup(ssml);
        if (string.IsNullOrWhiteSpace(mode.ModelId))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "Google Gemini speech synthesis requires mode.ModelId.");
        }

        var voiceName = ResolveGoogleGeminiVoiceName(mode);
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "Google Gemini speech synthesis requires VoiceName in the service mode preset.");
        }

        var apiKey = _configuration["GoogleGeminiApi:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "GoogleGeminiApi:ApiKey is required.");
        }

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{NormalizeGoogleGeminiModelName(mode.ModelId!)}:generateContent";
        var requestBody = new GoogleGeminiGenerateContentRequest(
            Contents:
            [
                new GoogleGeminiContent(
                    "user",
                    [
                        new GoogleGeminiPart(Text: text)
                    ])
            ],
            GenerationConfig: new GoogleGeminiGenerationConfig(
                ResponseModalities: ["AUDIO"],
                SpeechConfig: new GoogleGeminiSpeechConfig(
                    new GoogleGeminiVoiceConfig(
                        new GoogleGeminiPrebuiltVoiceConfig(voiceName)))));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-request-id", requestId);
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, $"Google Gemini speech synthesis failed: {(int)response.StatusCode} {body}");
        }

        var parsed = JsonSerializer.Deserialize<GoogleGeminiGenerateContentResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (!TryExtractGoogleGeminiAudioPart(parsed, out var audioPart))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "Google Gemini speech synthesis returned no audio.");
        }

        var rawBytes = Convert.FromBase64String(audioPart.Data);
        var outputBytes = IsWaveMimeType(audioPart.MimeType)
            ? rawBytes
            : WrapPcm16Mono24KhzAsWav(rawBytes);
        await File.WriteAllBytesAsync(outputPath, outputBytes, cancellationToken);
        var durationSeconds = rawBytes.Length > 0 ? (long)Math.Round(rawBytes.Length / (24000d * 2d)) : 0;
        return new ISpeechSynthesisService.SpeechSynthesisResult(true, durationSeconds);
    }

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaHuggingFaceAsync(
        string ssml,
        string outputPath,
        string requestId,
        ServiceMode mode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mode.ModelId))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "Hugging Face TTS requires mode.ModelId.");
        }

        var token = _configuration["HuggingFace:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "HuggingFace:Token is required.");
        }

        var routes = await ResolveHuggingFaceTtsProvidersAsync(mode.ModelId!, token, cancellationToken);
        var text = StripSsmlMarkup(ssml);
        string? lastError = null;

        foreach (var route in routes)
        {
            var endpoint = ResolveHuggingFaceTtsEndpoint(route);
            var requestPayload = BuildHuggingFaceTtsPayload(route, text);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestPayload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("x-request-id", requestId);
            if (string.Equals(route.Provider, "replicate", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("Prefer", "wait");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                lastError = $"{route.Provider}: {(int)response.StatusCode} {errorBody}";
                continue;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!IsHuggingFaceAudioMediaType(mediaType))
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (TryExtractHuggingFaceTtsAudioUrl(route.Provider, body, out var audioUrl))
                {
                    using var audioResponse = await _httpClient.GetAsync(audioUrl, cancellationToken);
                    if (!audioResponse.IsSuccessStatusCode)
                    {
                        var audioErrorBody = await audioResponse.Content.ReadAsStringAsync(cancellationToken);
                        lastError = $"{route.Provider}: failed to download audio from '{audioUrl}' - {(int)audioResponse.StatusCode} {audioErrorBody}";
                        continue;
                    }

                    var downloadedBytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    await File.WriteAllBytesAsync(outputPath, downloadedBytes, cancellationToken);
                    return new ISpeechSynthesisService.SpeechSynthesisResult(true, 0);
                }

                lastError = $"{route.Provider}: unexpected content-type '{mediaType ?? "<none>"}': {body}";
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
            return new ISpeechSynthesisService.SpeechSynthesisResult(true, ParseDurationSeconds(response));
        }

        return new ISpeechSynthesisService.SpeechSynthesisResult(
            false,
            0,
            $"Hugging Face TTS failed for model '{mode.ModelId}': {lastError ?? "No compatible live provider route was found."}");
    }

    private string ResolveHuggingFaceTtsEndpoint(HfProviderRoute route)
    {
        var configuredRouterBase = _configuration["HuggingFace:RouterBaseUrl"];
        var routerBase = string.IsNullOrWhiteSpace(configuredRouterBase)
            ? "https://router.huggingface.co/v1"
            : configuredRouterBase;

        var normalized = routerBase.TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^3];
        }

        if (string.Equals(route.Provider, "hf-inference", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/hf-inference/models/{route.ProviderId}";
        }

        if (string.Equals(route.Provider, "replicate", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/replicate/v1/models/{route.ProviderId}/predictions";
        }

        return $"{normalized}/{route.Provider}/{route.ProviderId}";
    }

    private static string BuildHuggingFaceTtsPayload(HfProviderRoute route, string text)
    {
        if (string.Equals(route.Provider, "replicate", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(
                new { input = new { text } },
                ProviderPayloadJson);
        }

        if (string.Equals(route.Provider, "fal-ai", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(
                new { text },
                ProviderPayloadJson);
        }

        return JsonSerializer.Serialize(new HuggingFaceTtsRequest(text), ProviderPayloadJson);
    }

    private static bool TryExtractHuggingFaceTtsAudioUrl(
        string provider,
        string responseBody,
        out string audioUrl)
    {
        audioUrl = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (string.Equals(provider, "replicate", StringComparison.OrdinalIgnoreCase)
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("output", out var outputNode))
            {
                if (outputNode.ValueKind == JsonValueKind.String)
                {
                    var value = outputNode.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        audioUrl = value;
                        return true;
                    }
                }

                if (outputNode.ValueKind == JsonValueKind.Array
                    && outputNode.GetArrayLength() > 0
                    && outputNode[0].ValueKind == JsonValueKind.String)
                {
                    var value = outputNode[0].GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        audioUrl = value;
                        return true;
                    }
                }
            }

            if (string.Equals(provider, "fal-ai", StringComparison.OrdinalIgnoreCase)
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("audio", out var audioNode)
                && audioNode.ValueKind == JsonValueKind.Object
                && audioNode.TryGetProperty("url", out var urlNode)
                && urlNode.ValueKind == JsonValueKind.String)
            {
                var value = urlNode.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    audioUrl = value;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private async Task<IReadOnlyList<HfProviderRoute>> ResolveHuggingFaceTtsProvidersAsync(
        string modelId,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://huggingface.co/api/models/{modelId}?expand[]=inferenceProviderMapping");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve Hugging Face providers for '{modelId}': {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("inferenceProviderMapping", out var mapping)
            || mapping.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Model '{modelId}' has no inference provider mapping.");
        }

        var routes = new List<HfProviderRoute>();
        foreach (var provider in mapping.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var status = provider.Value.TryGetProperty("status", out var statusNode)
                ? statusNode.GetString()
                : null;
            var task = provider.Value.TryGetProperty("task", out var taskNode)
                ? taskNode.GetString()
                : null;
            var providerId = provider.Value.TryGetProperty("providerId", out var providerIdNode)
                ? providerIdNode.GetString()
                : null;

            if (!string.Equals(status, "live", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(providerId))
            {
                continue;
            }

            if (string.Equals(task, "text-to-speech", StringComparison.OrdinalIgnoreCase)
                || string.Equals(task, "text-to-audio", StringComparison.OrdinalIgnoreCase))
            {
                routes.Add(new HfProviderRoute(provider.Name, providerId));
            }
        }

        if (routes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No live Hugging Face inference provider found for model '{modelId}' and task 'text-to-speech'.");
        }

        return routes;
    }

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaOpenRouterAsync(
        string ssml,
        string outputPath,
        string requestId,
        ServiceMode mode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mode.ModelId))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "OpenRouter TTS requires mode.ModelId.");
        }

        var apiKey = _configuration["OpenRouter:ApiKey"];
        var baseUrl = _configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "OpenRouter:ApiKey is required.");
        }

        var endpoint = $"{baseUrl.TrimEnd('/')}/audio/speech";
        var voiceName = ResolveOpenRouterVoiceName(mode);
        var payload = JsonSerializer.Serialize(
            new OpenRouterTtsRequest(
                StripSsmlMarkup(ssml),
                mode.ModelId,
                voiceName,
                ResponseFormat: "pcm"),
            ProviderPayloadJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("x-request-id", requestId);
        AddOpenRouterAttributionHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ISpeechSynthesisService.SpeechSynthesisResult(
                false,
                0,
                $"OpenRouter TTS failed (model={mode.ModelId}, voice={voiceName}): {(int)response.StatusCode} {errorBody}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(
                false,
                0,
                "OpenRouter TTS returned an empty audio payload.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var errorBody = Encoding.UTF8.GetString(bytes);
            return new ISpeechSynthesisService.SpeechSynthesisResult(
                false,
                0,
                $"OpenRouter TTS returned JSON instead of audio: {errorBody}");
        }

        if (string.Equals(mediaType, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(
                false,
                0,
                "OpenRouter TTS returned MP3 audio. Configure response_format=pcm for WAV output.");
        }

        var outputBytes = ResolveOpenRouterSpeechOutputBytes(bytes, mediaType);
        await File.WriteAllBytesAsync(outputPath, outputBytes, cancellationToken);
        return new ISpeechSynthesisService.SpeechSynthesisResult(true, 0);
    }

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaOpenAiAsync(
        string ssml,
        string outputPath,
        string requestId,
        ServiceMode mode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mode.ModelId))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "OpenAI TTS requires mode.ModelId.");
        }

        var voiceName = ResolveGoogleGeminiVoiceName(mode);
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "OpenAI TTS requires VoiceName in the service mode preset.");
        }

        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "OpenAI:ApiKey is required.");
        }

        var baseUrl = (_configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1").TrimEnd('/');
        var endpoint = $"{baseUrl}/audio/speech";
        var text = StripSsmlMarkup(ssml);
        var payload = JsonSerializer.Serialize(new OpenAiTtsRequest(mode.ModelId!, text, voiceName, "wav"), ProviderPayloadJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("x-request-id", requestId);

        _logger.LogInformation(
            "tts_api_request_start provider={Provider} requestId={RequestId} textLength={TextLength}",
            OpenAiProviderSection,
            requestId,
            text.Length);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, $"OpenAI TTS failed: {(int)response.StatusCode} {errorBody}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);

        _logger.LogInformation(
            "tts_api_request_success provider={Provider} requestId={RequestId} outputBytes={OutputBytes}",
            OpenAiProviderSection,
            requestId,
            bytes.Length);

        return new ISpeechSynthesisService.SpeechSynthesisResult(true, 0);
    }

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaAzureAsync(
        string ssml,
        string outputPath,
        string requestId,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var maxRetries = Math.Max(0, _synthesisOptionsMonitor.CurrentValue.MaxRetries);
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var synthesizer = new SpeechSynthesizer(CreateSpeechConfig(), audioConfig: null);
                    var timeout = TimeSpan.FromSeconds(Math.Max(1, _synthesisOptionsMonitor.CurrentValue.TimeoutSeconds));
                    _logger.LogInformation(
                        "tts_api_request_start provider={Provider} requestId={RequestId} outputPath={OutputPath}",
                        AzureProviderSection,
                        requestId,
                        outputPath);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(timeout);
                    var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
                    var speakTask = synthesizer.SpeakSsmlAsync(ssml);
                    var completed = await Task.WhenAny(speakTask, timeoutTask);
                    if (completed != speakTask)
                    {
                        await synthesizer.StopSpeakingAsync();
                        var timeoutMessage = $"Speech synthesis timed out after {timeout.TotalSeconds:F0}s.";
                        _logger.LogError(
                            "tts_api_request_failed provider={Provider} requestId={RequestId} reason={Reason}",
                            AzureProviderSection,
                            requestId,
                            timeoutMessage);
                        return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, timeoutMessage);
                    }

                    var result = await speakTask;
                    if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                    {
                        await File.WriteAllBytesAsync(outputPath, result.AudioData, cancellationToken);
                        var durationSeconds = (long)Math.Round(result.AudioDuration.TotalSeconds);
                        _logger.LogInformation(
                            "tts_api_request_success provider={Provider} requestId={RequestId} durationSeconds={DurationSeconds} outputBytes={OutputBytes}",
                            AzureProviderSection,
                            requestId,
                            durationSeconds,
                            result.AudioData.Length);
                        return new ISpeechSynthesisService.SpeechSynthesisResult(true, durationSeconds);
                    }

                    var details = SpeechSynthesisCancellationDetails.FromResult(result);
                    var message = $"Speech synthesis failed: {details.Reason} | {details.ErrorDetails}";
                    _logger.LogError(
                        "tts_api_request_failed provider={Provider} requestId={RequestId} reason={Reason}",
                        AzureProviderSection,
                        requestId,
                        message);
                    if (attempt < maxRetries && IsRetryableSynthesisFailure(details))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
                        continue;
                    }

                    return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, message);
                }
                catch (Exception ex)
                {
                    var message = $"Speech synthesis exception for {outputPath}: {ex.Message}";
                    _logger.LogError(
                        ex,
                        "tts_api_request_failed provider={Provider} requestId={RequestId} reason={Reason}",
                        AzureProviderSection,
                        requestId,
                        message);
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
                        continue;
                    }

                    return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, message);
                }
            }

            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "Speech synthesis failed without returning a result.");
        }, cancellationToken);
    }

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaLocalTtsAsync(
        string ssml,
        string outputPath,
        string requestId,
        ServiceMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            var localOptions = _synthesisOptionsMonitor.CurrentValue;
            var localHosts = _localServiceHostsOptionsMonitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(localHosts.SpeechSynthesisBaseUrl))
            {
                throw new InvalidOperationException(
                    "LocalServiceHosts:SpeechSynthesisBaseUrl is required for the local TTS provider.");
            }

            var plainText = StripSsmlMarkup(ssml);
            if (string.IsNullOrWhiteSpace(plainText))
            {
                throw new InvalidOperationException("Speech synthesis input is empty after SSML stripping.");
            }

            var endpoint = $"{localHosts.SpeechSynthesisBaseUrl.TrimEnd('/')}/tts/synthesize";
            var voiceName = ResolveLocalTtsVoiceName(mode);
            var speed = ResolveLocalTtsSpeed();

            // Wire contract (RULES I5/I9): .NET sends only { text, voice?, speed? }.
            // The TTS engine derives lang_code and any family-specific voice
            // semantics from the active catalog entry. No language map here.
            var payloadObject = new Dictionary<string, object>
            {
                ["text"] = plainText,
                ["speed"] = speed,
            };
            if (!string.IsNullOrWhiteSpace(voiceName))
            {
                payloadObject["voice"] = voiceName;
            }
            var payload = JsonSerializer.Serialize(payloadObject);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-request-id", requestId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, localOptions.TimeoutSeconds)));

            var startedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "tts_api_request_start provider={Provider} requestId={RequestId} textLength={TextLength} voice={VoiceName} speed={Speed}",
                LocalProviderSection,
                requestId,
                plainText.Length,
                string.IsNullOrWhiteSpace(voiceName) ? "(engine-default)" : voiceName,
                speed);

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "tts_api_request_failed provider={Provider} requestId={RequestId} statusCode={StatusCode} latencyMs={LatencyMs} errorBody={ErrorBody}",
                    LocalProviderSection,
                    requestId,
                    (int)response.StatusCode,
                    latencyMs,
                    errorBody);
                return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, $"Local TTS API failed: {response.StatusCode} - {errorBody}");
            }

            var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(outputPath, audioBytes, cancellationToken);

            var durationSeconds = ParseDurationSeconds(response);
            _logger.LogInformation(
                "tts_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} durationSeconds={DurationSeconds} outputBytes={OutputBytes}",
                LocalProviderSection,
                requestId,
                latencyMs,
                durationSeconds,
                audioBytes.Length);

            return new ISpeechSynthesisService.SpeechSynthesisResult(true, durationSeconds);
        }
        catch (Exception ex)
        {
            var message = $"Local speech synthesis exception for {outputPath}: {ex.Message}";
            _logger.LogError(
                ex,
                "tts_api_request_failed provider={Provider} requestId={RequestId} reason={Reason}",
                LocalProviderSection,
                requestId,
                message);
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, message);
        }
    }

    private SpeechConfig CreateSpeechConfig()
    {
        var c = _azureOptionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.ApiKey) || string.IsNullOrWhiteSpace(c.Region))
        {
            throw new InvalidOperationException("AzureSpeechService: ApiKey and Region must be configured.");
        }

        var speechConfig = SpeechConfig.FromSubscription(c.ApiKey, c.Region);
        if (!string.IsNullOrWhiteSpace(c.Endpoint))
        {
            speechConfig.SetProperty("SpeechServiceConnection_Endpoint", c.Endpoint);
        }

        return speechConfig;
    }

    private static bool IsRetryableSynthesisFailure(SpeechSynthesisCancellationDetails details)
    {
        return details.Reason == CancellationReason.Error
            && (details.ErrorCode == CancellationErrorCode.ServiceTimeout
                || details.ErrorCode == CancellationErrorCode.ConnectionFailure
                || details.ErrorCode == CancellationErrorCode.ServiceUnavailable);
    }

    private static long ParseDurationSeconds(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-audio-duration-seconds", out var values))
        {
            return 0;
        }

        var raw = values.FirstOrDefault();
        if (raw is null)
        {
            return 0;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds))
        {
            return 0;
        }

        return (long)Math.Round(durationSeconds);
    }

    private static string StripSsmlMarkup(string input)
    {
        var withoutTags = SsmlTagRegex.Replace(input, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static string? ResolveGoogleGeminiVoiceName(ServiceMode mode)
        => ResolveServiceModePresetString(mode, "VoiceName");

    // Returns the operator/user-configured voice selection for the local TTS
    // provider, or null when none is configured. When null, the engine uses
    // the active catalog entry's default voice. The meaning of the string
    // (voice-pack preset id, builtin speaker id, or design text) is resolved
    // by tts_service.py per the loaded family — .NET does not interpret it and
    // keeps no hardcoded voice enum or language map.
    private string? ResolveLocalTtsVoiceName(ServiceMode mode)
    {
        var voiceName = ResolveServiceModePresetString(mode, "VoiceName")
            ?? _configuration["SpeechSynthesis:VoiceName"]
            ?? _configuration["GA_TTS_VOICE"];
        return string.IsNullOrWhiteSpace(voiceName) ? null : voiceName.Trim();
    }

    private double ResolveLocalTtsSpeed()
    {
        var raw = _configuration["SpeechSynthesis:Speed"]
            ?? _configuration["GA_TTS_SPEED"];
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
        {
            return Math.Clamp(speed, 0.25, 4.0);
        }

        return 1.0;
    }

    private static string? ResolveServiceModePresetString(ServiceMode mode, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(mode.RequestPresetJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(mode.RequestPresetJson);
            if (document.RootElement.TryGetProperty(fieldName, out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string ResolveOpenRouterVoiceName(ServiceMode mode)
    {
        var explicitVoice = ResolveGoogleGeminiVoiceName(mode);
        if (!string.IsNullOrWhiteSpace(explicitVoice))
        {
            return explicitVoice;
        }

        if (!string.IsNullOrWhiteSpace(mode.ModelId)
            && mode.ModelId.Contains("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            // OpenRouter Kokoro models use prefixed voice ids; af_alloy is the closest default to generic "alloy".
            return "af_alloy";
        }

        return "alloy";
    }

    private void AddOpenRouterAttributionHeaders(HttpRequestMessage request) =>
        OpenRouterAttribution.Apply(request);

    private static string NormalizeGoogleGeminiModelName(string modelId)
    {
        var trimmed = modelId.Trim();
        return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"models/{trimmed}";
    }

    private static bool TryExtractGoogleGeminiAudioPart(
        GoogleGeminiGenerateContentResponse? response,
        out GoogleGeminiBlob audioPart)
    {
        audioPart = null!;
        var match = response?.Candidates?
            .SelectMany(candidate => candidate.Content?.Parts ?? [])
            .FirstOrDefault(part => part.InlineData != null);
        if (match?.InlineData == null)
        {
            return false;
        }

        audioPart = match.InlineData;
        return true;
    }

    private static bool IsWaveMimeType(string? mimeType) =>
        string.Equals(mimeType, "audio/wav", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "audio/x-wav", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mimeType, "audio/wave", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithRiffHeader(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4
        && bytes[0] == (byte)'R'
        && bytes[1] == (byte)'I'
        && bytes[2] == (byte)'F'
        && bytes[3] == (byte)'F';

    private static byte[] ResolveOpenRouterSpeechOutputBytes(byte[] bytes, string? mediaType)
    {
        if (IsWaveMimeType(mediaType) || StartsWithRiffHeader(bytes))
        {
            return bytes;
        }

        // OpenRouter defaults to raw 16-bit mono PCM at 24 kHz for compatible TTS models.
        return WrapPcm16Mono24KhzAsWav(bytes);
    }

    private static bool IsHuggingFaceAudioMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] WrapPcm16Mono24KhzAsWav(byte[] pcmBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        const short channels = 1;
        const int sampleRate = 24000;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmBytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmBytes.Length);
        writer.Write(pcmBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed record GoogleGeminiGenerateContentRequest(
        IReadOnlyList<GoogleGeminiContent> Contents,
        GoogleGeminiGenerationConfig GenerationConfig);

    private sealed record GoogleGeminiGenerationConfig(
        IReadOnlyList<string> ResponseModalities,
        GoogleGeminiSpeechConfig SpeechConfig);

    private sealed record GoogleGeminiSpeechConfig(GoogleGeminiVoiceConfig VoiceConfig);

    private sealed record GoogleGeminiVoiceConfig(GoogleGeminiPrebuiltVoiceConfig PrebuiltVoiceConfig);

    private sealed record GoogleGeminiPrebuiltVoiceConfig(string VoiceName);

    private sealed record GoogleGeminiContent(
        string Role,
        IReadOnlyList<GoogleGeminiPart> Parts);

    private sealed record GoogleGeminiPart(
        string? Text = null,
        GoogleGeminiBlob? InlineData = null);

    private sealed record GoogleGeminiBlob(string MimeType, string Data);

    private sealed record GoogleGeminiGenerateContentResponse(IReadOnlyList<GoogleGeminiCandidate>? Candidates);

    private sealed record GoogleGeminiCandidate(GoogleGeminiContent? Content);

    private sealed record HuggingFaceTtsRequest(string Inputs);

    private sealed record OpenRouterTtsRequest(
        string Input,
        string Model,
        string Voice,
        [property: JsonPropertyName("response_format")] string ResponseFormat);

    private sealed record OpenAiTtsRequest(
        string Model,
        string Input,
        string Voice,
        [property: JsonPropertyName("response_format")] string ResponseFormat);
}
