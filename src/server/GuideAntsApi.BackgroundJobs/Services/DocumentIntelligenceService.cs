using System.Net.Http;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.Services.Routing;
using HtmlAgility;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.BackgroundJobs.Services;

public interface IDocumentIntelligenceService
{
    Task<string> ExtractMarkdownAsync(Stream content, string fileName, CancellationToken cancellationToken = default);
    bool IsFileTypeSupported(string fileName, string contentType);
    bool IsFileSizeSupported(long fileSizeBytes);
}

public interface IDocumentIntelligenceExtractor
{
    string Provider { get; }
    Task<string> ExtractMarkdownAsync(Stream content, string fileName, ServiceMode mode, CancellationToken cancellationToken = default);
}

public sealed class ProviderRoutedDocumentIntelligenceService : IDocumentIntelligenceService
{
    private const string AzureProviderSection = "AzureDocumentIntelligence";
    private const string LocalProviderSection = "LocalServiceHosts:DocumentIntelligenceBaseUrl";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".html", ".htm",
        ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".heif"
    };

    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "text/html", "application/xhtml+xml",
        "image/jpeg", "image/jpg", "image/png", "image/bmp", "image/tiff", "image/heif"
    };

    private readonly Dictionary<string, IDocumentIntelligenceExtractor> _extractorsByProvider;
    private readonly IServiceModeResolver _serviceModeResolver;
    private readonly MarkdownExtractionOptions _extractionOptions;
    private readonly ILogger<ProviderRoutedDocumentIntelligenceService> _logger;

    public ProviderRoutedDocumentIntelligenceService(
        IEnumerable<IDocumentIntelligenceExtractor> extractors,
        IServiceModeResolver serviceModeResolver,
        IOptions<MarkdownExtractionOptions> extractionOptions,
        ILogger<ProviderRoutedDocumentIntelligenceService> logger)
    {
        _extractorsByProvider = extractors
            .GroupBy(e => e.Provider.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key.ToLowerInvariant(),
                g => g.Last(),
                StringComparer.OrdinalIgnoreCase);
        _serviceModeResolver = serviceModeResolver;
        _extractionOptions = extractionOptions.Value;
        _logger = logger;
    }

    public async Task<string> ExtractMarkdownAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        if (!IsFileTypeSupported(fileName, string.Empty))
        {
            throw new NotSupportedException($"File type not supported for markdown extraction: {fileName}");
        }

        if (content.CanSeek && !IsFileSizeSupported(content.Length))
        {
            throw new NotSupportedException($"File size {content.Length} bytes exceeds maximum allowed size");
        }

        if (IsHtmlFile(fileName))
        {
            return await ExtractMarkdownFromHtmlAsync(content, fileName, cancellationToken);
        }

        var mode = await _serviceModeResolver
            .ResolveAsync(RoutedServiceNames.DocumentIntelligence, modeId: null, cancellationToken)
            .ConfigureAwait(false);

        var providerId = mode.ProviderSection switch
        {
            AzureProviderSection => ServiceProviderIds.DocumentIntelligenceAzure,
            LocalProviderSection => ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp,
            _ => throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[]
                {
                    $"DocumentIntelligence mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                    $"Expected '{AzureProviderSection}' or '{LocalProviderSection}'."
                },
                serviceId: RoutedServiceNames.DocumentIntelligence,
                modeId: mode.ModeId)
        };

        if (!_extractorsByProvider.TryGetValue(providerId, out var extractor))
        {
            throw RoutingException.ProviderNotReady(
                mode.ProviderSection,
                new[] { $"No IDocumentIntelligenceExtractor is registered for provider '{providerId}'." },
                serviceId: RoutedServiceNames.DocumentIntelligence,
                modeId: mode.ModeId);
        }

        return await extractor.ExtractMarkdownAsync(content, fileName, mode, cancellationToken);
    }

    public bool IsFileTypeSupported(string fileName, string contentType)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        var isExtensionSupported = SupportedExtensions.Contains(extension);
        var isContentTypeSupported = string.IsNullOrWhiteSpace(contentType) || SupportedContentTypes.Contains(contentType);
        var isConfiguredExtensionSupported = !_extractionOptions.SupportedExtensions.Any()
            || _extractionOptions.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

        return isExtensionSupported && isContentTypeSupported && isConfiguredExtensionSupported;
    }

    public bool IsFileSizeSupported(long fileSizeBytes)
    {
        var maxSizeBytes = (long)_extractionOptions.MaxFileSizeMB * 1024 * 1024;
        return fileSizeBytes <= maxSizeBytes;
    }

    private static bool IsHtmlFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ExtractMarkdownFromHtmlAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        try
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            var htmlContent = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(htmlContent))
            {
                _logger.LogWarning("HTML file {FileName} is empty", fileName);
                return string.Empty;
            }

            var htmlDocument = new HtmlDocument();
            htmlDocument.LoadHtml(htmlContent);

            var result = htmlDocument.ConvertToMarkdown();
            _logger.LogInformation(
                "Extracted {Length} chars of markdown from HTML file {FileName}",
                result.Content.Length,
                fileName);

            return result.Content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting markdown from HTML file {FileName}", fileName);
            throw;
        }
    }
}

internal sealed class AzureDocumentIntelligenceExtractor(
    IOptionsMonitor<AzureDocumentIntelligenceOptions> optionsMonitor,
    IOptionsMonitor<DocumentIntelligenceOptions> serviceOptionsMonitor,
    ILogger<AzureDocumentIntelligenceExtractor> logger) : IDocumentIntelligenceExtractor
{
    private readonly IOptionsMonitor<AzureDocumentIntelligenceOptions> _optionsMonitor = optionsMonitor;
    private readonly IOptionsMonitor<DocumentIntelligenceOptions> _serviceOptionsMonitor = serviceOptionsMonitor;
    private readonly ILogger<AzureDocumentIntelligenceExtractor> _logger = logger;

    public string Provider => ServiceProviderIds.DocumentIntelligenceAzure;

    public async Task<string> ExtractMarkdownAsync(
        Stream content,
        string fileName,
        ServiceMode mode,
        CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "AzureDocumentIntelligence:Endpoint and AzureDocumentIntelligence:ApiKey are required for the Azure Document Intelligence provider.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException("AzureDocumentIntelligence:Endpoint must be an absolute URI.");
        }

        try
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            var credential = new AzureKeyCredential(options.ApiKey);
            var clientOptions = new DocumentIntelligenceClientOptions(ResolveServiceVersion(mode.RequestPresetJson))
            {
                Retry =
                {
                    MaxRetries = Math.Max(0, ReadIntPreset(mode.RequestPresetJson, "MaxRetries") ?? 3),
                    NetworkTimeout = TimeSpan.FromSeconds(Math.Max(1, _serviceOptionsMonitor.CurrentValue.TimeoutSeconds))
                }
            };
            var client = new DocumentIntelligenceClient(endpointUri, credential, clientOptions);
            var documentContent = BinaryData.FromStream(content);
            var analyzeOptions = new AnalyzeDocumentOptions("prebuilt-read", documentContent)
            {
                OutputContentFormat = DocumentContentFormat.Markdown
            };

            var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken);
            var markdown = operation.Value.Content ?? string.Empty;

            _logger.LogInformation(
                "Extracted {Length} chars of markdown from {FileName} via Azure Document Intelligence",
                markdown.Length,
                fileName);

            return markdown;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(
                ex,
                "Azure Document Intelligence API error for file {FileName}: {ErrorCode} - {Message}",
                fileName,
                ex.ErrorCode,
                ex.Message);
            throw;
        }
    }

    private static int? ReadIntPreset(string? requestPresetJson, string fieldName)
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

            return node.ValueKind switch
            {
                JsonValueKind.Number when node.TryGetInt32(out var value) => value,
                JsonValueKind.String when int.TryParse(node.GetString(), out var value) => value,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DocumentIntelligenceClientOptions.ServiceVersion ResolveServiceVersion(string? requestPresetJson)
    {
        var configured = ReadStringPreset(requestPresetJson, "ApiVersion");
        if (string.IsNullOrWhiteSpace(configured)
            || string.Equals(configured, "2024-11-30", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentIntelligenceClientOptions.ServiceVersion.V2024_11_30;
        }

        throw new InvalidOperationException(
            $"DocumentIntelligence Azure service mode ApiVersion '{configured}' is not supported by the installed Azure.AI.DocumentIntelligence client.");
    }

    private static string? ReadStringPreset(string? requestPresetJson, string fieldName)
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
}

internal sealed class DoclingServeDocumentIntelligenceExtractor(
    HttpClient client,
    IOptionsMonitor<DocumentIntelligenceOptions> optionsMonitor,
    IOptionsMonitor<LocalServiceHostsOptions> localServiceHostsOptionsMonitor,
    DoclingConversionLimiter conversionLimiter,
    ILogger<DoclingServeDocumentIntelligenceExtractor> logger) : IDocumentIntelligenceExtractor
{
    private const int MinAsyncPollIntervalMs = 250;

    private readonly HttpClient _client = client;
    private readonly IOptionsMonitor<DocumentIntelligenceOptions> _optionsMonitor = optionsMonitor;
    private readonly IOptionsMonitor<LocalServiceHostsOptions> _localServiceHostsOptionsMonitor = localServiceHostsOptionsMonitor;
    private readonly DoclingConversionLimiter _conversionLimiter = conversionLimiter;
    private readonly ILogger<DoclingServeDocumentIntelligenceExtractor> _logger = logger;

    public string Provider => ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp;

    public async Task<string> ExtractMarkdownAsync(
        Stream content,
        string fileName,
        ServiceMode mode,
        CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        var localHosts = _localServiceHostsOptionsMonitor.CurrentValue;
        if (string.IsNullOrWhiteSpace(localHosts.DocumentIntelligenceBaseUrl))
        {
            throw new InvalidOperationException(
                "LocalServiceHosts:DocumentIntelligenceBaseUrl is required for the local docling provider.");
        }
        var operationToken = cancellationToken;
        var hungAfter = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));

        var permitAcquired = false;
        try
        {
            await _conversionLimiter.WaitAsync(operationToken);
            permitAcquired = true;

            var baseUrl = localHosts.DocumentIntelligenceBaseUrl.TrimEnd('/');
            var taskId = await SubmitAsyncConversionAsync(
                baseUrl: baseUrl,
                content: content,
                fileName: fileName,
                options: options,
                operationToken: operationToken);

            var pollIntervalMs = Math.Max(MinAsyncPollIntervalMs, options.AsyncStatusPollIntervalMs);
            var lastSuccessfulPollAtUtc = DateTime.UtcNow;
            DoclingTaskStatus? latestStatus = null;
            while (true)
            {
                try
                {
                    latestStatus = await PollTaskStatusAsync(baseUrl, taskId, options, operationToken);
                    lastSuccessfulPollAtUtc = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (!operationToken.IsCancellationRequested)
                {
                    var stalledFor = DateTime.UtcNow - lastSuccessfulPollAtUtc;
                    if (stalledFor >= hungAfter)
                    {
                        throw new InvalidOperationException(
                            $"Docling async conversion task '{taskId}' appears hung: status polling has not succeeded for {stalledFor.TotalSeconds:F0}s.");
                    }

                    _logger.LogWarning(
                        "Transient timeout while polling docling task {TaskId}; last successful poll was {SecondsSinceLastPoll:F0}s ago.",
                        taskId,
                        stalledFor.TotalSeconds);

                    await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), operationToken);
                    continue;
                }

                if (IsTaskComplete(latestStatus.TaskStatus))
                {
                    break;
                }

                if (IsTaskFailure(latestStatus.TaskStatus))
                {
                    throw new InvalidOperationException(
                        $"Docling async conversion failed for task '{taskId}' with status '{latestStatus.TaskStatus}'. " +
                        $"{BuildTaskFailureDetails(latestStatus)}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(pollIntervalMs), operationToken);
            }

            var resultBody = await FetchTaskResultAsync(baseUrl, taskId, options, operationToken);
            var markdown = ParseMarkdown(resultBody);
            _logger.LogInformation(
                "Extracted {Length} chars of markdown from {FileName} via docling-serve async API (task {TaskId})",
                markdown.Length,
                fileName,
                taskId);

            return markdown;
        }
        finally
        {
            if (permitAcquired)
            {
                _conversionLimiter.Release();
            }
        }
    }

    private async Task<string> SubmitAsyncConversionAsync(
        string baseUrl,
        Stream content,
        string fileName,
        DocumentIntelligenceOptions options,
        CancellationToken operationToken)
    {
        var endpoint = $"{baseUrl}/v1/convert/file/async";
        var perRequestTimeoutSeconds = DoclingServeFormContentBuilder.ResolvePerRequestTimeoutSeconds(options);

        using var multipart = DoclingServeFormContentBuilder.BuildConversionForm(content, fileName, options);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = multipart
        };
        DoclingServeFormContentBuilder.ApplyAuthHeaders(request, options);

        using var requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        requestTimeoutCts.CancelAfter(TimeSpan.FromSeconds(perRequestTimeoutSeconds));

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeoutCts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(operationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Docling async submit failed ({(int)response.StatusCode}): {TruncateForLog(responseBody)}");
        }

        var taskId = ParseTaskId(responseBody);
        _logger.LogDebug("Submitted docling async conversion task {TaskId} for {FileName}", taskId, fileName);
        return taskId;
    }

    private async Task<DoclingTaskStatus> PollTaskStatusAsync(
        string baseUrl,
        string taskId,
        DocumentIntelligenceOptions options,
        CancellationToken operationToken)
    {
        var endpoint = $"{baseUrl}/v1/status/poll/{taskId}";
        var perRequestTimeoutSeconds = DoclingServeFormContentBuilder.ResolvePerRequestTimeoutSeconds(options);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        DoclingServeFormContentBuilder.ApplyAuthHeaders(request, options);

        using var requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        requestTimeoutCts.CancelAfter(TimeSpan.FromSeconds(perRequestTimeoutSeconds));

        using var response = await _client.SendAsync(request, requestTimeoutCts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(operationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Docling status poll failed for task '{taskId}' ({(int)response.StatusCode}): {TruncateForLog(responseBody)}");
        }

        return ParseTaskStatus(responseBody);
    }

    private async Task<string> FetchTaskResultAsync(
        string baseUrl,
        string taskId,
        DocumentIntelligenceOptions options,
        CancellationToken operationToken)
    {
        var endpoint = $"{baseUrl}/v1/result/{taskId}";
        var perRequestTimeoutSeconds = DoclingServeFormContentBuilder.ResolvePerRequestTimeoutSeconds(options);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        DoclingServeFormContentBuilder.ApplyAuthHeaders(request, options);

        using var requestTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        requestTimeoutCts.CancelAfter(TimeSpan.FromSeconds(perRequestTimeoutSeconds));

        using var response = await _client.SendAsync(request, requestTimeoutCts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(operationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Docling result fetch failed for task '{taskId}' ({(int)response.StatusCode}): {TruncateForLog(responseBody)}");
        }

        return responseBody;
    }

    private static string ParseTaskId(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (TryReadString(document.RootElement, out var taskId, "task_id", "taskId")
            && !string.IsNullOrWhiteSpace(taskId))
        {
            return taskId;
        }

        throw new InvalidOperationException("Docling async submit response does not include task_id.");
    }

    private static DoclingTaskStatus ParseTaskStatus(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!TryReadString(root, out var status, "task_status", "taskStatus")
            || string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidOperationException("Docling status response does not include task_status.");
        }

        TryReadString(root, out var taskId, "task_id", "taskId");
        TryReadInt(root, out var taskPosition, "task_position", "taskPosition");
        TryReadString(root, out var error, "error", "task_error", "taskError", "message");

        return new DoclingTaskStatus(
            TaskId: taskId ?? string.Empty,
            TaskStatus: status,
            TaskPosition: taskPosition,
            Error: error);
    }

    private static bool IsTaskComplete(string taskStatus) =>
        string.Equals(taskStatus, "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsTaskFailure(string taskStatus) =>
        string.Equals(taskStatus, "failure", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskStatus, "failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskStatus, "error", StringComparison.OrdinalIgnoreCase)
        || string.Equals(taskStatus, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static string BuildTaskFailureDetails(DoclingTaskStatus status)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(status.Error))
        {
            parts.Add($"Error: {status.Error}");
        }

        if (status.TaskPosition.HasValue)
        {
            parts.Add($"QueuePosition: {status.TaskPosition.Value}");
        }

        return parts.Count == 0 ? "No additional error details returned by docling." : string.Join(" ", parts);
    }

    private static bool TryReadString(JsonElement node, out string? value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (node.TryGetProperty(propertyName, out var valueNode) && valueNode.ValueKind == JsonValueKind.String)
            {
                value = valueNode.GetString();
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryReadInt(JsonElement node, out int? value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!node.TryGetProperty(propertyName, out var valueNode))
            {
                continue;
            }

            if (valueNode.ValueKind == JsonValueKind.Number && valueNode.TryGetInt32(out var intValue))
            {
                value = intValue;
                return true;
            }

            if (valueNode.ValueKind == JsonValueKind.String
                && int.TryParse(valueNode.GetString(), out var parsedValue))
            {
                value = parsedValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string ParseMarkdown(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (TryGetMarkdown(document.RootElement, out var markdown))
        {
            return markdown;
        }

        throw new InvalidOperationException("Docling response does not include markdown content.");
    }

    private static bool TryGetMarkdown(JsonElement root, out string markdown)
    {
        markdown = string.Empty;
        if (TryGetMarkdownFromPayload(root, out markdown))
        {
            return true;
        }

        if (root.TryGetProperty("result", out var resultNode))
        {
            return TryGetMarkdownFromPayload(resultNode, out markdown);
        }

        return false;
    }

    private static bool TryGetMarkdownFromPayload(JsonElement root, out string markdown)
    {
        markdown = string.Empty;
        if (root.TryGetProperty("document", out var documentNode))
        {
            if (TryReadMarkdown(documentNode, out markdown))
            {
                return true;
            }
        }

        if (!root.TryGetProperty("documents", out var documentsNode)
            || documentsNode.ValueKind != JsonValueKind.Array
            || documentsNode.GetArrayLength() == 0)
        {
            return false;
        }

        var first = documentsNode[0];
        if (TryReadMarkdown(first, out markdown))
        {
            return true;
        }

        if (first.TryGetProperty("document", out var nestedDocument))
        {
            return TryReadMarkdown(nestedDocument, out markdown);
        }

        return false;
    }

    private static bool TryReadMarkdown(JsonElement node, out string markdown)
    {
        markdown = string.Empty;
        if (!node.TryGetProperty("md_content", out var markdownNode))
        {
            return false;
        }

        if (markdownNode.ValueKind == JsonValueKind.String)
        {
            markdown = markdownNode.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static string TruncateForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const int maxChars = 2048;
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return $"{trimmed[..maxChars]}...<truncated:{trimmed.Length - maxChars}>";
    }

    private sealed record DoclingTaskStatus(
        string TaskId,
        string TaskStatus,
        int? TaskPosition,
        string? Error);
}
