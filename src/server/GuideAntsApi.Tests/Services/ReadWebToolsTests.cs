using System.Net;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.DataModel;
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

        result.Content.Should().Contain("Error:");
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

        result.Content.Should().Contain("Error:");
        browser.CallCount.Should().Be(1);
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
