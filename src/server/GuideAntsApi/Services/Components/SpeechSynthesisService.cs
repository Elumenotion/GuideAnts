using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
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

    private static readonly Regex SsmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<AzureSpeechServiceOptions> _azureOptionsMonitor;
    private readonly IOptionsMonitor<SpeechSynthesisOptions> _synthesisOptionsMonitor;
    private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly ILogger<SpeechSynthesisService> _logger;

    public SpeechSynthesisService(
        HttpClient httpClient,
        IOptionsMonitor<AzureSpeechServiceOptions> azureOptionsMonitor,
        IOptionsMonitor<SpeechSynthesisOptions> synthesisOptionsMonitor,
        IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptionsMonitor,
        IServiceModeResolver serviceModeResolver,
        ILogger<SpeechSynthesisService> logger)
    {
        _httpClient = httpClient;
        _azureOptionsMonitor = azureOptionsMonitor;
        _synthesisOptionsMonitor = synthesisOptionsMonitor;
        _localServiceHostsOptionsMonitor = localServiceHostsOptionsMonitor;
        _serviceModeResolver = serviceModeResolver;
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
            _ => throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[]
                {
                    $"SpeechSynthesis mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                    $"Expected '{AzureProviderSection}' or '{LocalProviderSection}'."
                },
                serviceId: RoutedServiceNames.SpeechSynthesis,
                modeId: mode.ModeId)
        };
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
}
