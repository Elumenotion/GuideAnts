using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components
{
    public class SpeechTranscriptionService : ISpeechTranscriptionService
    {
        private const string AzureProviderSection = "AzureSpeechService";
        private const string LocalProviderSection = "LocalServiceHosts:SpeechTranscriptionBaseUrl";
        private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
        private const string HuggingFaceProviderSection = "HuggingFace";
        private const string OpenRouterProviderSection = "OpenRouter";
        private const string OpenAiProviderSection = "OpenAI";
        private static readonly JsonSerializerOptions ProviderPayloadJson = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<AzureSpeechServiceOptions> _speechOptionsMonitor;
        private readonly IOptionsMonitor<SpeechTranscriptionOptions> _transcriptionOptionsMonitor;
        private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor;
        private readonly IOptionsMonitor<MarkdownExtractionOptions> _extractionOptionsMonitor;
        private readonly IVideoAudioExtractionService _videoAudioExtractionService;
        private readonly IServiceModeResolver _serviceModeResolver;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SpeechTranscriptionService> _logger;

        public SpeechTranscriptionService(
            HttpClient httpClient,
            IOptionsMonitor<AzureSpeechServiceOptions> speechOptions,
            IOptionsMonitor<SpeechTranscriptionOptions> transcriptionOptions,
            IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptions,
            IOptionsMonitor<MarkdownExtractionOptions> extractionOptions,
            IVideoAudioExtractionService videoAudioExtractionService,
            IServiceModeResolver serviceModeResolver,
            IConfiguration configuration,
            ILogger<SpeechTranscriptionService> logger)
        {
            _httpClient = httpClient;
            _speechOptionsMonitor = speechOptions;
            _transcriptionOptionsMonitor = transcriptionOptions;
            _localServiceHostsOptionsMonitor = localServiceHostsOptions;
            _extractionOptionsMonitor = extractionOptions;
            _videoAudioExtractionService = videoAudioExtractionService;
            _serviceModeResolver = serviceModeResolver;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<string> TranscribeAudioAsync(Stream audioContent, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            var result = await TranscribeAudioWithDurationAsync(audioContent, fileName, contentType, cancellationToken);
            return result.Text;
        }

        public async Task<TranscriptionResult> TranscribeAudioWithDurationAsync(Stream audioContent, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            // Default to diarization enabled for backward compatibility (file uploads, meeting recordings)
            return await TranscribeAudioWithDurationAsync(audioContent, fileName, contentType, enableDiarization: true, cancellationToken);
        }

        public async Task<TranscriptionResult> TranscribeAudioWithDurationAsync(Stream audioContent, string fileName, string contentType, bool enableDiarization, CancellationToken cancellationToken = default)
        {
            if (!IsAudioFileSupported(fileName, contentType))
            {
                throw new ArgumentException($"Unsupported audio/video file type: {fileName} (Content-Type: {contentType})");
            }

            if (!IsFileSizeSupported(audioContent.Length))
            {
                throw new ArgumentException($"Audio/video file too large: {audioContent.Length} bytes. Maximum supported size is {_extractionOptionsMonitor.CurrentValue.MaxFileSizeMB} MB.");
            }

            var mode = await _serviceModeResolver
                .ResolveAsync(RoutedServiceNames.SpeechTranscription, modeId: null, cancellationToken)
                .ConfigureAwait(false);

            if (_videoAudioExtractionService.IsVideoFileSupported(fileName, contentType)
                && string.Equals(mode.ProviderSection, LocalProviderSection, StringComparison.Ordinal))
            {
                return await TranscribeVideoFileWithDurationAsync(audioContent, fileName, contentType, enableDiarization, mode, cancellationToken);
            }

            try
            {
                return await TranscribeDirectAudioWithDurationAsync(audioContent, fileName, contentType, enableDiarization, mode, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error during transcription for {FileName}", fileName);
                throw new InvalidOperationException($"Failed to transcribe audio: {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Transcription timed out for {FileName}", fileName);
                var timeoutSeconds = GetEffectiveTimeoutSeconds(mode.ProviderSection);
                throw new TimeoutException($"Audio transcription timed out after {timeoutSeconds} seconds", ex);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse transcription response for {FileName}", fileName);
                throw new InvalidOperationException($"Failed to parse transcription response: {ex.Message}", ex);
            }
        }

        public bool IsAudioFileSupported(string fileName, string contentType)
        {
            return IsDirectAudioFileSupported(fileName, contentType) ||
                   _videoAudioExtractionService.IsVideoFileSupported(fileName, contentType);
        }

        private bool IsDirectAudioFileSupported(string fileName, string contentType)
        {
            if (!string.IsNullOrEmpty(contentType))
            {
                var lowerContentType = contentType.ToLowerInvariant();
                var supportedContentTypes = new[]
                {
                    "audio/wav", "audio/wave", "audio/mpeg", "audio/mp3", "audio/mp4",
                    "audio/aac", "audio/ogg", "audio/flac", "audio/x-ms-wma",
                    "audio/amr", "audio/webm", "audio/opus"
                };

                if (supportedContentTypes.Any(ct => lowerContentType.StartsWith(ct, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!string.IsNullOrEmpty(extension))
            {
                var supportedExtensions = new[]
                {
                    ".wav", ".mp3", ".ogg", ".flac", ".wma", ".aac", ".amr", ".webm", ".opus"
                };

                return supportedExtensions.Contains(extension) ||
                       _extractionOptionsMonitor.CurrentValue.SupportedExtensions.Any(ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private async Task<TranscriptionResult> TranscribeVideoFileWithDurationAsync(Stream videoContent, string fileName, string contentType, bool enableDiarization, ServiceMode mode, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing video file {FileName} for audio extraction and transcription", fileName);

            if (videoContent.CanSeek)
            {
                videoContent.Position = 0;
            }

            await using var extractedAudio = await _videoAudioExtractionService.ExtractAudioToTempFileAsync(
                videoContent,
                fileName,
                cancellationToken);

            _logger.LogInformation("Audio extracted from video {FileName}, now transcribing...", fileName);

            await using var audioStream = new FileStream(
                extractedAudio.AudioFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var result = await TranscribeDirectAudioWithDurationAsync(
                audioStream,
                Path.GetFileName(extractedAudio.AudioFilePath),
                extractedAudio.ContentType,
                enableDiarization,
                mode,
                cancellationToken);

            _logger.LogInformation(
                "Successfully transcribed video file {FileName}: {Length} characters, {Duration} seconds",
                fileName,
                result.Text.Length,
                result.DurationSeconds);

            return result;
        }

        private async Task<TranscriptionResult> TranscribeDirectAudioWithDurationAsync(Stream audioContent, string fileName, string contentType, bool enableDiarization, ServiceMode mode, CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var payloadSizeBytes = TryGetStreamLength(audioContent);
            var payloadSizeBucket = BuildPayloadSizeBucket(payloadSizeBytes);
            var normalizedContentType = NormalizeAudioContentType(fileName, contentType);

            _logger.LogWarning(
                "asr_api_request_start provider={Provider} requestId={RequestId} fileName={FileName} contentType={ContentType} diarization={Diarization} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket}",
                mode.ProviderSection,
                requestId,
                fileName,
                contentType,
                enableDiarization,
                payloadSizeBytes,
                payloadSizeBucket);

            return mode.ProviderSection switch
            {
                LocalProviderSection => await TranscribeViaLocalAsrWithDurationAsync(audioContent, fileName, normalizedContentType, requestId, payloadSizeBytes, payloadSizeBucket, cancellationToken),
                AzureProviderSection => await TranscribeViaAzureSpeechWithDurationAsync(audioContent, fileName, normalizedContentType, enableDiarization, requestId, payloadSizeBytes, payloadSizeBucket, cancellationToken),
                GoogleGeminiProviderSection => await TranscribeViaGoogleGeminiWithDurationAsync(
                    audioContent, fileName, normalizedContentType, requestId, payloadSizeBytes, payloadSizeBucket, mode, cancellationToken),
                HuggingFaceProviderSection => await TranscribeViaHuggingFaceWithDurationAsync(
                    audioContent, fileName, normalizedContentType, requestId, payloadSizeBytes, payloadSizeBucket, mode, cancellationToken),
                OpenRouterProviderSection => await TranscribeViaOpenRouterWithDurationAsync(
                    audioContent, fileName, normalizedContentType, requestId, payloadSizeBytes, payloadSizeBucket, mode, cancellationToken),
                OpenAiProviderSection => await TranscribeViaOpenAiWithDurationAsync(
                    audioContent, fileName, normalizedContentType, requestId, payloadSizeBytes, payloadSizeBucket, mode, cancellationToken),
                _ => throw RoutingException.ProviderNotReady(
                    mode.ProviderSection,
                    new[]
                    {
                        $"SpeechTranscription mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                        $"Expected '{AzureProviderSection}', '{LocalProviderSection}', '{GoogleGeminiProviderSection}', '{HuggingFaceProviderSection}', '{OpenRouterProviderSection}', or '{OpenAiProviderSection}'."
                    },
                    serviceId: RoutedServiceNames.SpeechTranscription,
                    modeId: mode.ModeId)
            };
        }

        private async Task<TranscriptionResult> TranscribeViaGoogleGeminiWithDurationAsync(
            Stream audioContent,
            string fileName,
            string contentType,
            string requestId,
            long payloadSizeBytes,
            string payloadSizeBucket,
            ServiceMode mode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mode.ModelId))
            {
                throw RoutingException.ProviderNotReady(
                    GoogleGeminiProviderSection,
                    new[] { $"SpeechTranscription mode '{mode.ModeId}' requires a Google Gemini transcription model id." },
                    serviceId: RoutedServiceNames.SpeechTranscription,
                    modeId: mode.ModeId);
            }
            ValidateGoogleGeminiTranscriptionModel(mode.ModelId!);

            var apiKey = _configurationForSection("GoogleGeminiApi", "ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
            }
            audioContent.Position = 0;
            await using var memory = new MemoryStream();
            await audioContent.CopyToAsync(memory, cancellationToken);
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{NormalizeGoogleGeminiModelName(mode.ModelId!)}:generateContent";
            var requestBody = new GoogleGeminiGenerateContentRequest(
                Contents:
                [
                    new GoogleGeminiContent(
                        "user",
                        [
                            new GoogleGeminiPart(Text: "Transcribe this audio. Return only the transcript."),
                            new GoogleGeminiPart(
                                InlineData: new GoogleGeminiBlob(
                                    ResolveGoogleGeminiAudioMimeType(fileName, contentType),
                                    Convert.ToBase64String(memory.ToArray())))
                        ])
                ]);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-request-id", requestId);
            request.Headers.Add("x-goog-api-key", apiKey);

            var startedAt = DateTime.UtcNow;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google Gemini transcription failed ({(int)response.StatusCode}): {body}");
            }

            var parsed = JsonSerializer.Deserialize<GoogleGeminiGenerateContentResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new GoogleGeminiGenerateContentResponse(Array.Empty<GoogleGeminiCandidate>());
            var transcript = string.Join(" ", parsed.Candidates
                .SelectMany(candidate => candidate.Content?.Parts ?? [])
                .Select(part => part.Text)
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            _logger.LogWarning(
                "asr_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} durationSeconds={DurationSeconds} textLength={TextLength}",
                GoogleGeminiProviderSection,
                requestId,
                latencyMs,
                payloadSizeBytes,
                payloadSizeBucket,
                0,
                transcript.Length);

            return new TranscriptionResult(transcript, 0);
        }

        private async Task<TranscriptionResult> TranscribeViaHuggingFaceWithDurationAsync(
            Stream audioContent,
            string fileName,
            string contentType,
            string requestId,
            long payloadSizeBytes,
            string payloadSizeBucket,
            ServiceMode mode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mode.ModelId))
            {
                throw RoutingException.ProviderNotReady(
                    HuggingFaceProviderSection,
                    new[] { $"SpeechTranscription mode '{mode.ModeId}' requires a Hugging Face ASR model id." },
                    serviceId: RoutedServiceNames.SpeechTranscription,
                    modeId: mode.ModeId);
            }

            var token = _configurationForSection("HuggingFace", "Token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("HuggingFace:Token is required.");
            }

            audioContent.Position = 0;
            await using var memory = new MemoryStream();
            await audioContent.CopyToAsync(memory, cancellationToken);
            var endpoint = ResolveHuggingFaceAsrEndpoint(mode.ModelId!);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = includeTimestamps
                    ? new StringContent(
                        JsonSerializer.Serialize(
                            new HuggingFaceAsrRequest(
                                Inputs: Convert.ToBase64String(memory.ToArray()),
                                Parameters: new HuggingFaceAsrParameters(ReturnTimestamps: true)),
                            ProviderPayloadJson),
                        Encoding.UTF8,
                        "application/json")
                    : new ByteArrayContent(memory.ToArray())
            };
            if (!includeTimestamps)
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("x-request-id", requestId);

            var startedAt = DateTime.UtcNow;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Hugging Face ASR failed ({(int)response.StatusCode}): {body}");
            }

            var text = ParseHuggingFaceAsrText(body, includeTimestamps);
            _logger.LogWarning(
                "asr_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} durationSeconds={DurationSeconds} textLength={TextLength}",
                HuggingFaceProviderSection,
                requestId,
                latencyMs,
                payloadSizeBytes,
                payloadSizeBucket,
                0,
                text.Length);

            return new TranscriptionResult(text, 0);
        }

        private string ResolveHuggingFaceAsrEndpoint(string modelId)
        {
            var configuredRouterBase = _configurationForSection("HuggingFace", "RouterBaseUrl");
            var routerBase = string.IsNullOrWhiteSpace(configuredRouterBase)
                ? "https://router.huggingface.co/v1"
                : configuredRouterBase;

            var normalized = routerBase.TrimEnd('/');
            if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^3];
            }

            return $"{normalized}/hf-inference/models/{modelId}";
        }

        private static bool ResolveHuggingFaceTimestampPreference(string? requestPresetJson)
        {
            var explicitReturnTimestamps = ReadServiceModePresetField(requestPresetJson, "ReturnTimestamps");
            if (bool.TryParse(explicitReturnTimestamps, out var parsedReturnTimestamps))
            {
                return parsedReturnTimestamps;
            }

            var explicitTimestamps = ReadServiceModePresetField(requestPresetJson, "Timestamps");
            if (bool.TryParse(explicitTimestamps, out var parsedTimestamps))
            {
                return parsedTimestamps;
            }

            return false;
        }

        private static string ParseHuggingFaceAsrText(string body, bool includeTimestamps)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("text", out var textNode)
                    && textNode.ValueKind == JsonValueKind.String)
                {
                    var plainText = textNode.GetString() ?? string.Empty;
                    if (!includeTimestamps)
                    {
                        return plainText;
                    }

                    if (root.TryGetProperty("chunks", out var chunksNode)
                        && chunksNode.ValueKind == JsonValueKind.Array)
                    {
                        var timestamped = BuildTimestampedAsrText(chunksNode);
                        if (!string.IsNullOrWhiteSpace(timestamped))
                        {
                            return timestamped;
                        }
                    }

                    return plainText;
                }
            }

            var fallback = JsonSerializer.Deserialize<HuggingFaceAsrResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return fallback?.Text ?? string.Empty;
        }

        private static string BuildTimestampedAsrText(JsonElement chunksNode)
        {
            var lines = new List<string>();
            foreach (var chunk in chunksNode.EnumerateArray())
            {
                if (chunk.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var text = chunk.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String
                    ? textNode.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var prefix = string.Empty;
                if (chunk.TryGetProperty("timestamp", out var timestampNode)
                    && timestampNode.ValueKind == JsonValueKind.Array
                    && timestampNode.GetArrayLength() >= 2)
                {
                    var start = timestampNode[0].ToString();
                    var end = timestampNode[1].ToString();
                    prefix = $"[{start}-{end}] ";
                }

                lines.Add(prefix + text.Trim());
            }

            return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        }

        private async Task<TranscriptionResult> TranscribeViaOpenRouterWithDurationAsync(
            Stream audioContent,
            string fileName,
            string contentType,
            string requestId,
            long payloadSizeBytes,
            string payloadSizeBucket,
            ServiceMode mode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mode.ModelId))
            {
                throw RoutingException.ProviderNotReady(
                    OpenRouterProviderSection,
                    new[] { $"SpeechTranscription mode '{mode.ModeId}' requires an OpenRouter model id." },
                    serviceId: RoutedServiceNames.SpeechTranscription,
                    modeId: mode.ModeId);
            }

            var apiKey = _configurationForSection("OpenRouter", "ApiKey");
            var baseUrl = _configurationForSection("OpenRouter", "BaseUrl") ?? "https://openrouter.ai/api/v1";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenRouter:ApiKey is required.");
            }

            audioContent.Position = 0;
            await using var memory = new MemoryStream();
            await audioContent.CopyToAsync(memory, cancellationToken);
            var audioBytes = memory.ToArray();
            var maxBytes = ResolveOpenRouterAudioMaxBytes(mode.RequestPresetJson);
            if (audioBytes.LongLength > maxBytes)
            {
                throw new InvalidOperationException(
                    $"OpenRouter transcription payload exceeds configured limit ({audioBytes.LongLength} > {maxBytes} bytes).");
            }
            var endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
            var format = ResolveOpenRouterAudioFormat(fileName, contentType);
            var requestBody = new OpenRouterTranscriptionRequest(
                Model: mode.ModelId!,
                Messages:
                [
                    new OpenRouterTranscriptionMessage(
                        Role: "user",
                        Content:
                        [
                            new OpenRouterTranscriptionContentText("text", "Transcribe this audio."),
                            new OpenRouterTranscriptionContentAudio(
                                "input_audio",
                                new OpenRouterInputAudio(Convert.ToBase64String(audioBytes), format))
                        ])
                ]);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("x-request-id", requestId);

            var startedAt = DateTime.UtcNow;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenRouter transcription failed ({(int)response.StatusCode}): {body}");
            }

            var parsed = JsonSerializer.Deserialize<OpenRouterTranscriptionResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            _logger.LogWarning(
                "asr_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} durationSeconds={DurationSeconds} textLength={TextLength}",
                OpenRouterProviderSection,
                requestId,
                latencyMs,
                payloadSizeBytes,
                payloadSizeBucket,
                0,
                text.Length);

            return new TranscriptionResult(text, 0);
        }

        private async Task<TranscriptionResult> TranscribeViaOpenAiWithDurationAsync(
            Stream audioContent,
            string fileName,
            string contentType,
            string requestId,
            long payloadSizeBytes,
            string payloadSizeBucket,
            ServiceMode mode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(mode.ModelId))
            {
                throw RoutingException.ProviderNotReady(
                    OpenAiProviderSection,
                    new[] { $"SpeechTranscription mode '{mode.ModeId}' requires an OpenAI transcription model id." },
                    serviceId: RoutedServiceNames.SpeechTranscription,
                    modeId: mode.ModeId);
            }

            var apiKey = _configurationForSection("OpenAI", "ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenAI:ApiKey is required.");
            }

            var baseUrl = (_configurationForSection("OpenAI", "Endpoint") ?? "https://api.openai.com/v1").TrimEnd('/');
            var endpoint = $"{baseUrl}/audio/transcriptions";

            audioContent.Position = 0;
            using var content = new MultipartFormDataContent();
            var audioStreamContent = new StreamContent(audioContent);
            audioStreamContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            content.Add(audioStreamContent, "file", fileName);
            content.Add(new StringContent(mode.ModelId!), "model");
            content.Add(new StringContent("json"), "response_format");

            var language = ReadServiceModePresetField(mode.RequestPresetJson, "language");
            if (!string.IsNullOrWhiteSpace(language))
            {
                content.Add(new StringContent(language), "language");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("x-request-id", requestId);

            var startedAt = DateTime.UtcNow;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI transcription failed ({(int)response.StatusCode}): {body}");
            }

            var parsed = JsonSerializer.Deserialize<OpenAiTranscriptionResponse>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var text = parsed?.Text ?? string.Empty;
            _logger.LogWarning(
                "asr_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} durationSeconds={DurationSeconds} textLength={TextLength}",
                OpenAiProviderSection,
                requestId,
                latencyMs,
                payloadSizeBytes,
                payloadSizeBucket,
                0,
                text.Length);

            return new TranscriptionResult(text, 0);
        }

        private string? _configurationForSection(string section, string field)
        {
            return _configuration[$"{section}:{field}"];
        }

        private static string ResolveOpenRouterAudioFormat(string fileName, string contentType)
        {
            var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ext))
            {
                if (IsOpenRouterAudioFormatSupported(ext))
                {
                    return ext;
                }

                throw new InvalidOperationException($"OpenRouter transcription does not support audio format '{ext}'.");
            }

            if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
            if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase)) return "wav";
            if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return "ogg";
            if (contentType.Contains("webm", StringComparison.OrdinalIgnoreCase)) return "webm";
            if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
            if (contentType.Contains("m4a", StringComparison.OrdinalIgnoreCase) || contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase)) return "m4a";

            throw new InvalidOperationException(
                $"OpenRouter transcription content type '{contentType}' is not in the supported audio format set.");
        }

        private static bool IsOpenRouterAudioFormatSupported(string format) =>
            format is "wav" or "mp3" or "ogg" or "webm" or "flac" or "m4a";

        private long ResolveOpenRouterAudioMaxBytes(string? requestPresetJson)
        {
            var configured = ReadServiceModePresetField(requestPresetJson, "MaxAudioBytes");
            if (long.TryParse(configured, out var value) && value > 0)
            {
                return value;
            }

            return 25L * 1024 * 1024;
        }

        private void ValidateGoogleGeminiTranscriptionModel(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new InvalidOperationException("Google Gemini transcription model id is required.");
            }
        }

        private static string? ReadServiceModePresetField(string? requestPresetJson, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(requestPresetJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(requestPresetJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(fieldName, out var node))
                {
                    return null;
                }

                return node.ValueKind == JsonValueKind.String
                    ? node.GetString()?.Trim()
                    : node.ToString().Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string NormalizeGoogleGeminiModelName(string modelId)
        {
            var trimmed = modelId.Trim();
            return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"models/{trimmed}";
        }

        private static string ResolveGoogleGeminiAudioMimeType(string fileName, string contentType)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                return contentType;
            }

            var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            return extension switch
            {
                "wav" => "audio/wav",
                "mp3" => "audio/mpeg",
                "ogg" => "audio/ogg",
                "flac" => "audio/flac",
                "webm" => "audio/webm",
                "aac" => "audio/aac",
                "m4a" => "audio/mp4",
                _ => "application/octet-stream"
            };
        }

        private static string NormalizeAudioContentType(string fileName, string? contentType)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                var candidate = contentType.Trim();
                var separatorIndex = candidate.IndexOf(';');
                if (separatorIndex >= 0)
                {
                    candidate = candidate[..separatorIndex];
                }

                candidate = candidate.Trim();
                if (candidate.Length > 0)
                {
                    return candidate;
                }
            }

            return ResolveGoogleGeminiAudioMimeType(fileName, contentType ?? string.Empty);
        }

        private async Task<TranscriptionResult> TranscribeViaLocalAsrWithDurationAsync(
            Stream audioContent,
            string fileName,
            string contentType,
            string requestId,
            long payloadSizeBytes,
            string payloadSizeBucket,
            CancellationToken cancellationToken)
        {
            audioContent.Position = 0;

            var localOptions = _transcriptionOptionsMonitor.CurrentValue;
            var localHosts = _localServiceHostsOptionsMonitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(localHosts.SpeechTranscriptionBaseUrl))
            {
                throw new InvalidOperationException(
                    "LocalServiceHosts:SpeechTranscriptionBaseUrl is required for the local ASR provider.");
            }

            var endpoint = localHosts.SpeechTranscriptionBaseUrl.TrimEnd('/');
            var apiUrl = $"{endpoint}/asr/transcribe";

            using var content = new MultipartFormDataContent();
            var audioStreamContent = new StreamContent(audioContent);
            audioStreamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(audioStreamContent, "audio", fileName);

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };
            requestMessage.Headers.Add("x-request-id", requestId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, localOptions.TimeoutSeconds)));

            var startedAt = DateTime.UtcNow;
            var response = await _httpClient.SendAsync(requestMessage, timeoutCts.Token);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "asr_api_request_failed provider={Provider} requestId={RequestId} statusCode={StatusCode} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} errorBody={ErrorBody}",
                    LocalProviderSection,
                    requestId,
                    (int)response.StatusCode,
                    latencyMs,
                    payloadSizeBytes,
                    payloadSizeBucket,
                    responseContent);
                throw new InvalidOperationException($"Local ASR API failed: {response.StatusCode} - {responseContent}");
            }

            var result = JsonSerializer.Deserialize<LocalAsrTranscriptionResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var text = result?.Text ?? string.Empty;
            var durationSeconds = result?.DurationSeconds ?? 0;

            _logger.LogWarning(
                "asr_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} durationSeconds={DurationSeconds} textLength={TextLength} modelRef={ModelRef}",
                LocalProviderSection,
                requestId,
                latencyMs,
                payloadSizeBytes,
                payloadSizeBucket,
                durationSeconds,
                text.Length,
                result?.ModelRef);

            return new TranscriptionResult(text, durationSeconds);
        }

        private async Task<TranscriptionResult> TranscribeViaAzureSpeechWithDurationAsync(
            Stream audioContent,
            string fileName,
            string contentType,
            bool enableDiarization,
            string requestId,
            long payloadSizeBytes,
            string payloadSizeBucket,
            CancellationToken cancellationToken)
        {
            audioContent.Position = 0;
            await using var audioBuffer = new MemoryStream();
            await audioContent.CopyToAsync(audioBuffer, cancellationToken);
            var audioBytes = audioBuffer.ToArray();

            var speechOptions = _speechOptionsMonitor.CurrentValue;
            var endpoint = speechOptions.Endpoint.TrimEnd('/');
            var apiUrl = $"{endpoint}/speechtotext/transcriptions:transcribe?api-version=2024-11-15";

            object definition = enableDiarization
                ? new
                {
                    locales = new[] { "en-US" },
                    profanityFilterMode = "Masked",
                    diarization = new
                    {
                        enabled = true,
                        maxSpeakers = 10
                    }
                }
                : new
                {
                    locales = new[] { "en-US" },
                    profanityFilterMode = "Masked"
                };

            var definitionJson = JsonSerializer.Serialize(definition);

            var startedAt = DateTime.UtcNow;
            HttpResponseMessage? response = null;
            var responseContent = string.Empty;
            var maxRetries = Math.Max(0, _transcriptionOptionsMonitor.CurrentValue.MaxRetries);
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var content = new MultipartFormDataContent();
                var audioStreamContent = new ByteArrayContent(audioBytes);
                audioStreamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                content.Add(audioStreamContent, "audio", fileName);
                content.Add(new StringContent(definitionJson, Encoding.UTF8, "application/json"), "definition");

                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, apiUrl)
                {
                    Content = content
                };
                requestMessage.Headers.Add("Ocp-Apim-Subscription-Key", speechOptions.ApiKey);
                requestMessage.Headers.Add("x-request-id", requestId);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _transcriptionOptionsMonitor.CurrentValue.TimeoutSeconds)));

                response?.Dispose();
                response = await _httpClient.SendAsync(requestMessage, timeoutCts.Token);
                responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode || !IsTransientStatus(response.StatusCode) || attempt == maxRetries)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken);
            }

            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (response == null)
            {
                throw new InvalidOperationException("Fast Transcription API did not return a response.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "asr_api_request_failed provider={Provider} requestId={RequestId} statusCode={StatusCode} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} errorBody={ErrorBody}",
                    AzureProviderSection,
                    requestId,
                    (int)response.StatusCode,
                    latencyMs,
                    payloadSizeBytes,
                    payloadSizeBucket,
                    responseContent);
                throw new InvalidOperationException($"Fast Transcription API failed: {response.StatusCode} - {responseContent}");
            }

            var transcriptionResult = JsonSerializer.Deserialize<FastTranscriptionResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (transcriptionResult?.Phrases == null || transcriptionResult.Phrases.Length == 0)
            {
                _logger.LogWarning("No speech recognized in extracted audio from {FileName}", fileName);
                return new TranscriptionResult(string.Empty, 0);
            }

            var transcribedText = enableDiarization
                ? FormatDiarizedTranscription(transcriptionResult.Phrases)
                : FormatPlainTranscription(transcriptionResult.Phrases);

            if (string.IsNullOrWhiteSpace(transcribedText))
            {
                _logger.LogWarning("Empty transcription result for extracted audio from {FileName}", fileName);
                return new TranscriptionResult(string.Empty, 0);
            }

            var durationSeconds = (long)Math.Round(transcriptionResult.DurationMilliseconds / 1000.0);
            _logger.LogWarning(
                "asr_api_request_success provider={Provider} requestId={RequestId} latencyMs={LatencyMs} payloadSizeBytes={PayloadSizeBytes} payloadSizeBucket={PayloadSizeBucket} durationSeconds={DurationSeconds} textLength={TextLength}",
                AzureProviderSection,
                requestId,
                latencyMs,
                payloadSizeBytes,
                payloadSizeBucket,
                durationSeconds,
                transcribedText.Length);

            return new TranscriptionResult(transcribedText, durationSeconds);
        }

        private static string FormatDiarizedTranscription(Phrase[] phrases)
        {
            if (phrases.Length == 0)
            {
                return string.Empty;
            }

            var result = new StringBuilder();
            int? currentSpeaker = null;
            var currentSpeakerText = new StringBuilder();

            foreach (var phrase in phrases.OrderBy(p => p.OffsetMilliseconds))
            {
                if (phrase.Speaker != currentSpeaker)
                {
                    if (currentSpeaker.HasValue && currentSpeakerText.Length > 0)
                    {
                        result.AppendLine($"**Speaker {currentSpeaker + 1}:** {currentSpeakerText.ToString().Trim()}");
                        result.AppendLine();
                        currentSpeakerText.Clear();
                    }

                    currentSpeaker = phrase.Speaker;
                }

                if (!string.IsNullOrWhiteSpace(phrase.Text))
                {
                    if (currentSpeakerText.Length > 0)
                    {
                        currentSpeakerText.Append(' ');
                    }

                    currentSpeakerText.Append(phrase.Text);
                }
            }

            if (currentSpeaker.HasValue && currentSpeakerText.Length > 0)
            {
                result.AppendLine($"**Speaker {currentSpeaker + 1}:** {currentSpeakerText.ToString().Trim()}");
            }

            return result.ToString().Trim();
        }

        private static string FormatPlainTranscription(Phrase[] phrases)
        {
            if (phrases.Length == 0)
            {
                return string.Empty;
            }

            var result = new StringBuilder();
            foreach (var phrase in phrases.OrderBy(p => p.OffsetMilliseconds))
            {
                if (!string.IsNullOrWhiteSpace(phrase.Text))
                {
                    if (result.Length > 0)
                    {
                        result.Append(' ');
                    }

                    result.Append(phrase.Text);
                }
            }

            return result.ToString().Trim();
        }

        private int GetEffectiveTimeoutSeconds(string providerSection)
        {
            if (string.Equals(providerSection, LocalProviderSection, StringComparison.Ordinal))
            {
                return Math.Max(1, _transcriptionOptionsMonitor.CurrentValue.TimeoutSeconds);
            }

            return Math.Max(1, _transcriptionOptionsMonitor.CurrentValue.TimeoutSeconds);
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return statusCode == HttpStatusCode.RequestTimeout
                || statusCode == (HttpStatusCode)429
                || code >= 500;
        }

        private static long TryGetStreamLength(Stream stream)
        {
            try
            {
                return stream.Length;
            }
            catch
            {
                return 0;
            }
        }

        private static string BuildPayloadSizeBucket(long fileSizeBytes)
        {
            if (fileSizeBytes < 512_000) return "lt_512kb";
            if (fileSizeBytes < 2_000_000) return "512kb_to_2mb";
            if (fileSizeBytes < 10_000_000) return "2mb_to_10mb";
            if (fileSizeBytes < 50_000_000) return "10mb_to_50mb";
            return "gte_50mb";
        }

        public bool IsFileSizeSupported(long fileSizeBytes)
        {
            var maxSizeBytes = Math.Min((long)_extractionOptionsMonitor.CurrentValue.MaxFileSizeMB * 1024 * 1024, 300L * 1024 * 1024);
            return fileSizeBytes <= maxSizeBytes;
        }
    }

    public class LocalAsrTranscriptionResponse
    {
        public string RequestId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public long DurationSeconds { get; set; }
        public string? ModelRef { get; set; }
    }

    public class FastTranscriptionResponse
    {
        public int DurationMilliseconds { get; set; }
        public CombinedPhrase[] CombinedPhrases { get; set; } = Array.Empty<CombinedPhrase>();
        public Phrase[] Phrases { get; set; } = Array.Empty<Phrase>();
    }

    public class CombinedPhrase
    {
        public string Text { get; set; } = string.Empty;
    }

    public class Phrase
    {
        public int OffsetMilliseconds { get; set; }
        public int DurationMilliseconds { get; set; }
        public string Text { get; set; } = string.Empty;
        public Word[] Words { get; set; } = Array.Empty<Word>();
        public string Locale { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public int? Speaker { get; set; }
    }

    public class Word
    {
        public string Text { get; set; } = string.Empty;
        public int OffsetMilliseconds { get; set; }
        public int DurationMilliseconds { get; set; }
    }

    public sealed record GoogleGeminiGenerateContentRequest(
        IReadOnlyList<GoogleGeminiContent> Contents);

    public sealed record GoogleGeminiContent(
        string Role,
        IReadOnlyList<GoogleGeminiPart> Parts);

    public sealed record GoogleGeminiPart(
        string? Text = null,
        GoogleGeminiBlob? InlineData = null);

    public sealed record GoogleGeminiBlob(string MimeType, string Data);

    public sealed record GoogleGeminiGenerateContentResponse(
        IReadOnlyList<GoogleGeminiCandidate> Candidates);

    public sealed record GoogleGeminiCandidate(GoogleGeminiContent? Content);

    public sealed record HuggingFaceAsrRequest(
        [property: JsonPropertyName("inputs")] string Inputs,
        [property: JsonPropertyName("parameters")] HuggingFaceAsrParameters Parameters);

    public sealed record HuggingFaceAsrParameters(
        [property: JsonPropertyName("return_timestamps")] bool ReturnTimestamps);

    public sealed record HuggingFaceAsrResponse(string? Text);

    public sealed record OpenRouterTranscriptionRequest(
        string Model,
        IReadOnlyList<OpenRouterTranscriptionMessage> Messages);

    public sealed record OpenRouterTranscriptionMessage(
        string Role,
        IReadOnlyList<object> Content);

    public sealed record OpenRouterTranscriptionContentText(
        string Type,
        string Text);

    public sealed record OpenRouterTranscriptionContentAudio(
        string Type,
        [property: JsonPropertyName("input_audio")] OpenRouterInputAudio InputAudio);

    public sealed record OpenRouterInputAudio(
        string Data,
        string Format);

    public sealed record OpenRouterTranscriptionResponse(
        IReadOnlyList<OpenRouterTranscriptionChoice>? Choices);

    public sealed record OpenRouterTranscriptionChoice(OpenRouterTranscriptionMessageOut? Message);

    public sealed record OpenRouterTranscriptionMessageOut(string? Content);

    public sealed record OpenAiTranscriptionResponse(string? Text);
}
