using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components;

/// <summary>
/// Concrete implementation of <see cref="ISpeechSynthesisService"/> supporting both Azure Speech and local TTS provider routing.
/// </summary>
public sealed class SpeechSynthesisService : ISpeechSynthesisService
{
    private const string AzureProviderSection = "AzureSpeechService";
    private const string LocalProviderSection = "LocalServiceHosts:SpeechSynthesisBaseUrl";
    private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";
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

        return mode.ProviderSection switch
        {
            LocalProviderSection => await SynthesizeViaLocalTtsAsync(ssml, outputPath, requestId, cancellationToken),
            AzureProviderSection => await SynthesizeViaAzureAsync(ssml, outputPath, requestId, cancellationToken),
            GoogleGeminiProviderSection => await SynthesizeViaGoogleAsync(ssml, outputPath, requestId, mode, cancellationToken),
            HuggingFaceProviderSection => await SynthesizeViaHuggingFaceAsync(ssml, outputPath, requestId, mode, cancellationToken),
            OpenRouterProviderSection => await SynthesizeViaOpenRouterAsync(ssml, outputPath, requestId, mode, cancellationToken),
            _ => throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[]
                {
                    $"SpeechSynthesis mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                    $"Expected '{AzureProviderSection}', '{LocalProviderSection}', '{GoogleGeminiProviderSection}', '{HuggingFaceProviderSection}', or '{OpenRouterProviderSection}'."
                },
                serviceId: RoutedServiceNames.SpeechSynthesis,
                modeId: mode.ModeId)
        };
    }

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
        ValidateHuggingFaceTtsModel(mode.ModelId);

        var token = _configuration["HuggingFace:Token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "HuggingFace:Token is required.");
        }

        var endpoint = $"https://api-inference.huggingface.co/models/{mode.ModelId}";
        var payload = JsonSerializer.Serialize(new HuggingFaceTtsRequest(StripSsmlMarkup(ssml)), ProviderPayloadJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("x-request-id", requestId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, $"Hugging Face TTS failed: {(int)response.StatusCode} {errorBody}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
        return new ISpeechSynthesisService.SpeechSynthesisResult(true, 0);
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
        ValidateOpenRouterTtsModel(mode.ModelId);

        var apiKey = _configuration["OpenRouter:ApiKey"];
        var baseUrl = _configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, "OpenRouter:ApiKey is required.");
        }

        var endpoint = $"{baseUrl.TrimEnd('/')}/tts";
        var payload = JsonSerializer.Serialize(
            new OpenRouterTtsRequest(
                StripSsmlMarkup(ssml),
                mode.ModelId,
                "alloy"),
            ProviderPayloadJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("x-request-id", requestId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, $"OpenRouter TTS failed: {(int)response.StatusCode} {errorBody}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
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
            try
            {
                using var synthesizer = new SpeechSynthesizer(CreateSpeechConfig(), audioConfig: null);
                var azureOptions = _azureOptionsMonitor.CurrentValue;
                var timeout = TimeSpan.FromSeconds(Math.Max(1, azureOptions.TimeoutSeconds));
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
                return new ISpeechSynthesisService.SpeechSynthesisResult(false, 0, message);
            }
        }, cancellationToken);
    }

    private async Task<ISpeechSynthesisService.SpeechSynthesisResult> SynthesizeViaLocalTtsAsync(
        string ssml,
        string outputPath,
        string requestId,
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
            var payload = JsonSerializer.Serialize(new { text = plainText });

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-request-id", requestId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, localOptions.TimeoutSeconds)));

            var startedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "tts_api_request_start provider={Provider} requestId={RequestId} textLength={TextLength}",
                LocalProviderSection,
                requestId,
                plainText.Length);

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
    {
        if (string.IsNullOrWhiteSpace(mode.RequestPresetJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(mode.RequestPresetJson);
            if (document.RootElement.TryGetProperty("VoiceName", out var voiceNameElement)
                && voiceNameElement.ValueKind == JsonValueKind.String)
            {
                return voiceNameElement.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

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

    private void ValidateHuggingFaceTtsModel(string modelId)
    {
        var configured = _configuration["HuggingFace:TtsAllowedModels"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsModelAllowed(modelId, configured))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Hugging Face TTS model '{modelId}' is not in HuggingFace:TtsAllowedModels.");
        }

        if (modelId.Contains("tts", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("speech", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("kokoro", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Hugging Face model '{modelId}' is not recognized as TTS-capable. " +
            "Set HuggingFace:TtsAllowedModels to explicitly allow it.");
    }

    private void ValidateOpenRouterTtsModel(string modelId)
    {
        var configured = _configuration["OpenRouter:TtsAllowedModels"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (IsModelAllowed(modelId, configured))
            {
                return;
            }

            throw new InvalidOperationException(
                $"OpenRouter TTS model '{modelId}' is not in OpenRouter:TtsAllowedModels.");
        }

        if (modelId.Contains("tts", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("speech", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"OpenRouter model '{modelId}' is not recognized as TTS-capable. " +
            "Set OpenRouter:TtsAllowedModels to explicitly allow it.");
    }

    private static bool IsModelAllowed(string modelId, string allowlistCsv)
    {
        var entries = allowlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(modelId, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        string Voice);
}
