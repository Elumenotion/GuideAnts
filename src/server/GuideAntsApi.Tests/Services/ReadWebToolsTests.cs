using System.Net;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class ReadWebToolsTests
{
    private ApplicationDbContext _context = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(options);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenDirectFetchSucceeds_ReturnsMarkdownAndDoesNotWriteExcludedHost()
    {
        var browser = new FakeBrowserRenderingClient();
        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><p>Hello direct</p></body></html>")
                })),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://example.com/page");

        result.Content.Should().Contain("Hello direct");
        browser.CallCount.Should().Be(0);
        (await _context.ExcludedHosts.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenDirectFetchStarts_ReportsReadWebActivity()
    {
        var activities = new List<ToolActivityUpdate>();
        var invocationId = Guid.NewGuid();
        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CurrentInvocationId = invocationId,
            InvocationDepth = 2,
            TriggeringToolCallId = "call-readweb",
            ToolActivitySink = activities.Add
        };

        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><p>Hello direct</p></body></html>")
                })),
            new FakeBrowserRenderingClient());

        _ = await ReadWebTools.GetContentFromUrl("https://example.com/page", context);

        activities.Should().ContainSingle();
        activities[0].Name.Should().Be("ReadWeb");
        activities[0].Status.Should().Be("running");
        activities[0].ToolCallId.Should().Be("call-readweb");
        activities[0].InvocationId.Should().Be(invocationId);
        activities[0].InvocationDepth.Should().Be(2);
        activities[0].Source.Should().Be("read_web");
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenDirectFetchFailsAndBrowserRenderSucceeds_ReturnsMarkdownAndDoesNotWriteExcludedHost()
    {
        var browser = new FakeBrowserRenderingClient
        {
            Result = new BrowserRenderedPageResult(
                true,
                "<html><body><p>Hello rendered</p></body></html>",
                null,
                "https://example.com/page",
                200)
        };

        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError))),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://example.com/page");

        result.Content.Should().Contain("Hello rendered");
        browser.CallCount.Should().Be(1);
        (await _context.ExcludedHosts.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenAccessDenied_WritesExcludedHost()
    {
        var browser = new FakeBrowserRenderingClient
        {
            Result = new BrowserRenderedPageResult(false, null, "forbidden", "https://example.com/page", 403)
        };

        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden))),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://example.com/page");

        result.Content.Should().Be(
            "403 Forbidden. This host blocks unauthenticated access. Do not retry — use a different source or local files.");
        browser.CallCount.Should().Be(1);

        var excludedHosts = await _context.ExcludedHosts.ToListAsync();
        excludedHosts.Should().ContainSingle();
        excludedHosts[0].Host.Should().Be("example.com");
        excludedHosts[0].Reason.Should().Contain("ReadWeb access denied");
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenFailuresAreNotAccessDenied_DoesNotWriteExcludedHost()
    {
        var browser = new FakeBrowserRenderingClient
        {
            Result = new BrowserRenderedPageResult(false, null, "render failed", "https://example.com/page", 500)
        };

        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError))),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://example.com/page");

        result.Content.Should().Be(
            "Server error (5xx). You may retry once; if it fails again, use a different source.");
        browser.CallCount.Should().Be(1);
        (await _context.ExcludedHosts.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenNotFound_ReturnsInstructiveMessage()
    {
        var browser = new FakeBrowserRenderingClient
        {
            Result = new BrowserRenderedPageResult(false, null, "HTTP 404", "https://example.com/missing", 404)
        };

        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.NotFound))),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://example.com/missing");

        result.Content.Should().Be(
            "404 Not Found. This URL does not exist. Do not retry — fix the path or use local file search.");
        browser.CallCount.Should().Be(1);
        (await _context.ExcludedHosts.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenInvalidUrl_ReturnsInstructiveMessage()
    {
        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK))),
            new FakeBrowserRenderingClient());

        var result = await ReadWebTools.GetContentFromUrl("not-a-url");

        result.Content.Should().Be("Invalid URL. Provide a complete https:// URL.");
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenHostExcluded_ReturnsBlockedMessageWithoutFetching()
    {
        _context.ExcludedHosts.Add(new ExcludedHost
        {
            Host = "blocked.example.com",
            Reason = "ReadWeb access denied.",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var browser = new FakeBrowserRenderingClient();
        var httpCalled = false;
        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
            {
                httpCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><p>Hello</p></body></html>")
                };
            })),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://blocked.example.com/page");

        result.Content.Should().Be(ReadWebHostPolicy.ExcludedHostMessage);
        httpCalled.Should().BeFalse();
        browser.CallCount.Should().Be(0);
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenProtectedHostIsExcludedInDatabase_StillFetches()
    {
        _context.ExcludedHosts.Add(new ExcludedHost
        {
            Host = "api.github.com",
            Reason = "ReadWeb access denied.",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        var browser = new FakeBrowserRenderingClient();
        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body><p>Hello github</p></body></html>")
                })),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://api.github.com/repos/example/example");

        result.Content.Should().Contain("Hello github");
        browser.CallCount.Should().Be(0);
    }

    [TestMethod]
    public async Task GetContentFromUrl_WhenAccessDeniedOnProtectedHost_DoesNotWriteExcludedHost()
    {
        var browser = new FakeBrowserRenderingClient
        {
            Result = new BrowserRenderedPageResult(false, null, "forbidden", "https://api.github.com/repos/example/example", 403)
        };

        InitializeTool(
            new FakeHttpClientFactory(new StaticHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden))),
            browser);

        var result = await ReadWebTools.GetContentFromUrl("https://api.github.com/repos/example/example");

        result.Content.Should().Be(
            "403 Forbidden. This host blocks unauthenticated access. Do not retry — use a different source or local files.");
        (await _context.ExcludedHosts.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public void GetContentFromUrl_IsRegisteredAsAnnotatedTool()
    {
        ToolContractRegistry.RefreshContracts();

        var tools = ToolContractRegistry.GetAllToolOperations();

        tools.Should().ContainKey("GetContentFromUrl");
        tools["GetContentFromUrl"].Should().Be("GuideAntsApi.Services.ReadWebTools.GetContentFromUrl");

        var contract = ToolContractRegistry.GetContract(tools["GetContentFromUrl"]);
        contract.RequiresNotebookContext.Should().BeTrue();
        contract.ParameterMetadata.Should().ContainKey("context");
        contract.ParameterMetadata["context"].Hidden.Should().BeTrue();

        var schema = ToolContractRegistry.GenerateOpenApiSchema(tools["GetContentFromUrl"]);
        schema.Should().Contain("\"operationId\": \"GetContentFromUrl\"");
        schema.Should().Contain("\"GuideAntsApi.Services.ReadWebTools.GetContentFromUrl\"");
        schema.Should().Contain("\"url\"");
        schema.Should().NotContain("\"context\"");
    }

    private void InitializeTool(IHttpClientFactory httpClientFactory, IBrowserRenderingClient browserRenderingClient)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(httpClientFactory);
        services.AddSingleton(browserRenderingClient);
        services.AddSingleton<IExcludedHostService>(
            new ExcludedHostService(_context, NullLogger<ExcludedHostService>.Instance));

        ReadWebTools.InitializeServiceProvider(services.BuildServiceProvider());
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeBrowserRenderingClient : IBrowserRenderingClient
    {
        public int CallCount { get; private set; }

        public BrowserRenderedPageResult Result { get; set; } =
            new(false, null, "not configured", null, null);

        public Task<BrowserRenderedPageResult> RenderHtmlAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}
