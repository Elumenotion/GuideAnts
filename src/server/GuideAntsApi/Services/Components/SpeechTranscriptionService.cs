using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

        private readonly HttpClient _httpClient;
        private readonly IOptionsMonitor<AzureSpeechServiceOptions> _speechOptionsMonitor;
        private readonly IOptionsMonitor<SpeechTranscriptionOptions> _transcriptionOptionsMonitor;
        private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor;
        private readonly IOptionsMonitor<MarkdownExtractionOptions> _extractionOptionsMonitor;
        private readonly IVideoAudioExtractionService _videoAudioExtractionService;
        private readonly IServiceModeResolver _serviceModeResolver;
        private readonly ILogger<SpeechTranscriptionService> _logger;

        public SpeechTranscriptionService(
            HttpClient httpClient,
            IOptionsMonitor<AzureSpeechServiceOptions> speechOptions,
            IOptionsMonitor<SpeechTranscriptionOptions> transcriptionOptions,
            IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptions,
            IOptionsMonitor<MarkdownExtractionOptions> extractionOptions,
            IVideoAudioExtractionService videoAudioExtractionService,
            IServiceModeResolver serviceModeResolver,
            ILogger<SpeechTranscriptionService> logger)
        {
            _httpClient = httpClient;
            _speechOptionsMonitor = speechOptions;
            _transcriptionOptionsMonitor = transcriptionOptions;
            _localServiceHostsOptionsMonitor = localServiceHostsOptions;
            _extractionOptionsMonitor = extractionOptions;
            _videoAudioExtractionService = videoAudioExtractionService;
            _serviceModeResolver = serviceModeResolver;
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
                LocalProviderSection => await TranscribeViaLocalAsrWithDurationAsync(audioContent, fileName, contentType, requestId, payloadSizeBytes, payloadSizeBucket, cancellationToken),
                AzureProviderSection => await TranscribeViaAzureSpeechWithDurationAsync(audioContent, fileName, contentType, enableDiarization, requestId, payloadSizeBytes, payloadSizeBucket, cancellationToken),
                _ => throw RoutingException.ProviderNotReady(
                    mode.ProviderSection,
                    new[]
                    {
                        $"SpeechTranscription mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                        $"Expected '{AzureProviderSection}' or '{LocalProviderSection}'."
                    },
                    serviceId: RoutedServiceNames.SpeechTranscription,
                    modeId: mode.ModeId)
            };
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

            var speechOptions = _speechOptionsMonitor.CurrentValue;
            var endpoint = speechOptions.Endpoint.TrimEnd('/');
            var apiUrl = $"{endpoint}/speechtotext/transcriptions:transcribe?api-version=2024-11-15";

            using var content = new MultipartFormDataContent();
            var audioStreamContent = new StreamContent(audioContent);
            audioStreamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(audioStreamContent, "audio", fileName);

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
            content.Add(new StringContent(definitionJson, Encoding.UTF8, "application/json"), "definition");

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };
            requestMessage.Headers.Add("Ocp-Apim-Subscription-Key", speechOptions.ApiKey);
            requestMessage.Headers.Add("x-request-id", requestId);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, speechOptions.TimeoutSeconds)));

            var startedAt = DateTime.UtcNow;
            var response = await _httpClient.SendAsync(requestMessage, timeoutCts.Token);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var latencyMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

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

            return Math.Max(1, _speechOptionsMonitor.CurrentValue.TimeoutSeconds);
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
}
