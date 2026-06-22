using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.BackgroundJobs;

/// <summary>
/// Branch coverage for the docling-serve async HTTP extractor using a fake
/// <see cref="HttpMessageHandler"/> that emulates the submit/poll/result protocol.
/// </summary>
[TestClass]
public sealed class DoclingServeDocumentIntelligenceExtractorTests
{
    private const string BaseUrl = "http://docling:5001";

    private static readonly ServiceMode Mode = new(
        ModeId: "default",
        ProviderSection: "LocalServiceHosts:DocumentIntelligenceBaseUrl",
        ModelId: null,
        RequestPresetJson: null,
        Enabled: true,
        IsDefault: true);

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenBaseUrlMissing()
    {
        var handler = new RoutedHandler();
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient, baseUrl: string.Empty);

        await using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DocumentIntelligenceBaseUrl is required*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_SendsConfiguredDoclingOptions_OnSubmit()
    {
        string? capturedBody = null;
        var handler = new RoutedHandler
        {
            OnSubmit = request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{\"task_id\":\"task-options\"}");
            },
            OnPoll = _ => Json("{\"task_status\":\"success\"}"),
            OnResult = _ => Json("{\"document\":{\"md_content\":\"configured\"}}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(
            httpClient,
            options: new DocumentIntelligenceOptions
            {
                TimeoutSeconds = 300,
                MaxConcurrentConversions = 2,
                AsyncStatusPollIntervalMs = 250,
                DoclingDoOcr = true,
                DoclingTableMode = "fast",
                DoclingApiKey = "docling-secret"
            });

        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("pdf-bytes"));
        var markdown = await extractor.ExtractMarkdownAsync(content, "report.pdf", Mode);

        markdown.Should().Be("configured");
        capturedBody.Should().NotBeNullOrEmpty();
        capturedBody.Should().Contain("name=do_ocr");
        capturedBody.Should().Contain("name=table_mode");
        handler.SubmitRequest!.Headers.TryGetValues("X-Api-Key", out var apiKeys).Should().BeTrue();
        apiKeys!.Single().Should().Be("docling-secret");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_ReturnsMarkdown_OnSuccessfulConversion()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"task-123\"}"),
            OnPoll = _ => Json("{\"task_status\":\"success\",\"task_id\":\"task-123\"}"),
            OnResult = _ => Json("{\"document\":{\"md_content\":\"# Hello World\"}}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("pdf-bytes"));
        var markdown = await extractor.ExtractMarkdownAsync(content, "report.pdf", Mode);

        markdown.Should().Be("# Hello World");
        handler.SubmitUri!.AbsolutePath.Should().Be("/v1/convert/file/async");
        handler.PollUri!.AbsolutePath.Should().Be("/v1/status/poll/task-123");
        handler.ResultUri!.AbsolutePath.Should().Be("/v1/result/task-123");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_PollsUntilSuccess_WhenStatusStartsPending()
    {
        var pollAttempts = 0;
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"task-9\"}"),
            OnPoll = _ =>
            {
                pollAttempts++;
                return pollAttempts < 2
                    ? Json("{\"task_status\":\"started\",\"task_position\":1}")
                    : Json("{\"task_status\":\"success\"}");
            },
            OnResult = _ => Json("{\"documents\":[{\"md_content\":\"chunked md\"}]}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient, pollIntervalMs: 250);

        await using var content = new MemoryStream(new byte[] { 4, 5, 6 });
        var markdown = await extractor.ExtractMarkdownAsync(content, "scan.png", Mode);

        markdown.Should().Be("chunked md");
        pollAttempts.Should().BeGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_ParsesMarkdown_FromNestedResultDocumentsArray()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"taskId\":\"camel-1\"}"),
            OnPoll = _ => Json("{\"taskStatus\":\"success\"}"),
            OnResult = _ => Json("{\"result\":{\"documents\":[{\"document\":{\"md_content\":\"nested md\"}}]}}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 7 });
        var markdown = await extractor.ExtractMarkdownAsync(content, "deck.pptx", Mode);

        markdown.Should().Be("nested md");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenSubmitFails()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("bad file", Encoding.UTF8, "text/plain")
            }
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*async submit failed (400)*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenSubmitResponseMissingTaskId()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"status\":\"queued\"}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not include task_id*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenPollReturnsFailureStatus()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"fail-1\"}"),
            OnPoll = _ => Json("{\"task_status\":\"failure\",\"error\":\"boom\",\"task_position\":3}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*conversion failed for task 'fail-1'*Error: boom*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenPollResponseMissingStatus()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"no-status\"}"),
            OnPoll = _ => Json("{\"task_id\":\"no-status\"}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not include task_status*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenPollReturnsErrorStatusCode()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"poll-500\"}"),
            OnPoll = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("server error", Encoding.UTF8, "text/plain")
            }
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*status poll failed for task 'poll-500' (500)*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenResultFetchFails()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"res-404\"}"),
            OnPoll = _ => Json("{\"task_status\":\"success\"}"),
            OnResult = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing", Encoding.UTF8, "text/plain")
            }
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*result fetch failed for task 'res-404' (404)*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenResultMissingMarkdown()
    {
        var handler = new RoutedHandler
        {
            OnSubmit = _ => Json("{\"task_id\":\"no-md\"}"),
            OnPoll = _ => Json("{\"task_status\":\"success\"}"),
            OnResult = _ => Json("{\"document\":{\"json_content\":{}}}")
        };
        using var httpClient = new HttpClient(handler);
        var extractor = CreateExtractor(httpClient);

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", Mode);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not include markdown content*");
    }

    private static DoclingServeDocumentIntelligenceExtractor CreateExtractor(
        HttpClient httpClient,
        string baseUrl = BaseUrl,
        int pollIntervalMs = 250,
        DocumentIntelligenceOptions? options = null)
    {
        var diOptions = options ?? new DocumentIntelligenceOptions
        {
            TimeoutSeconds = 300,
            MaxConcurrentConversions = 2,
            AsyncStatusPollIntervalMs = pollIntervalMs
        };
        var diMonitor = new StaticOptionsMonitor<DocumentIntelligenceOptions>(diOptions);
        var localMonitor = new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions
        {
            DocumentIntelligenceBaseUrl = baseUrl
        });
        var limiter = new DoclingConversionLimiter(diMonitor, NullLogger<DoclingConversionLimiter>.Instance);

        return new DoclingServeDocumentIntelligenceExtractor(
            httpClient,
            diMonitor,
            localMonitor,
            limiter,
            NullLogger<DoclingServeDocumentIntelligenceExtractor>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class RoutedHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OnSubmit { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? OnPoll { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? OnResult { get; set; }

        public Uri? SubmitUri { get; private set; }
        public HttpRequestMessage? SubmitRequest { get; private set; }
        public Uri? PollUri { get; private set; }
        public Uri? ResultUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/v1/convert/file/async", StringComparison.Ordinal))
            {
                SubmitUri = request.RequestUri;
                SubmitRequest = request;
                return Task.FromResult((OnSubmit ?? throw new InvalidOperationException("OnSubmit not configured"))(request));
            }

            if (path.Contains("/v1/status/poll/", StringComparison.Ordinal))
            {
                PollUri = request.RequestUri;
                return Task.FromResult((OnPoll ?? throw new InvalidOperationException("OnPoll not configured"))(request));
            }

            if (path.Contains("/v1/result/", StringComparison.Ordinal))
            {
                ResultUri = request.RequestUri;
                return Task.FromResult((OnResult ?? throw new InvalidOperationException("OnResult not configured"))(request));
            }

            throw new InvalidOperationException($"Unexpected request path: {path}");
        }
    }
}
