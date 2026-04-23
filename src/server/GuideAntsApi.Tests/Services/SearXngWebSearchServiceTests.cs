using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Options;
using GuideAntsApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class SearXngWebSearchServiceTests
{
    [TestMethod]
    public async Task SearchAsync_ConstructsExpectedSearXngRequest()
    {
        Uri? capturedRequestUri = null;
        var handler = new DelegatingHandlerStub((request, _) =>
        {
            capturedRequestUri = request.RequestUri;
            var payload = "{\"results\":[{\"title\":\"A\",\"url\":\"https://example.com/a\",\"content\":\"one\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(handler);

        var response = await service.SearchAsync("hello world", count: 1, skip: 0);

        response.Web.Results.Should().HaveCount(1);
        capturedRequestUri.Should().NotBeNull();
        capturedRequestUri!.Query.Should().Contain("format=json");
        capturedRequestUri.Query.Should().Contain("q=hello%20world");
        capturedRequestUri.Query.Should().Contain("language=en-US");
        capturedRequestUri.Query.Should().Contain("safesearch=1");
        capturedRequestUri.Query.Should().Contain("categories=general");
    }

    [TestMethod]
    public async Task SearchAsync_Paginates_Deduplicates_AndAppliesSkipCountFiltering()
    {
        var responsesByPage = new Dictionary<string, string>
        {
            ["1"] = "{" +
                        "\"results\":[" +
                        "{\"title\":\"A\",\"url\":\"https://allowed.com/a\",\"content\":\"one\"}," +
                        "{\"title\":\"A dup\",\"url\":\"https://allowed.com/a\",\"content\":\"dup\"}," +
                        "{\"title\":\"Blocked\",\"url\":\"https://blocked.com/x\",\"content\":\"block\"}," +
                        "{\"title\":\"B\",\"url\":\"https://allowed.com/b\",\"content\":\"two\"}" +
                        "]}",
            ["2"] = "{" +
                        "\"results\":[" +
                        "{\"title\":\"C\",\"url\":\"https://allowed.com/c\",\"content\":\"three\"}," +
                        "{\"title\":\"D\",\"url\":\"https://allowed.com/d\",\"content\":\"four\"}" +
                        "]}"
        };

        var handler = new DelegatingHandlerStub((request, _) =>
        {
            var page = GetQueryValue(request.RequestUri!, "pageno") ?? "1";
            var payload = responsesByPage.TryGetValue(page, out var value)
                ? value
                : "{\"results\":[]}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        var excludedHostService = new Mock<IExcludedHostService>();
        excludedHostService
            .Setup(x => x.GetExcludedHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked.com" });
        excludedHostService
            .Setup(x => x.IsHostExcluded(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>()))
            .Returns<string, IReadOnlySet<string>>((host, excluded) =>
            {
                if (excluded.Contains(host))
                    return true;
                return excluded.Any(x => host.EndsWith('.' + x, StringComparison.OrdinalIgnoreCase));
            });

        var options = Microsoft.Extensions.Options.Options.Create(new SearXngSearchOptions
        {
            BaseUrl = "http://localhost:8091",
            TimeoutMs = 15000,
            Language = "en-US",
            SafeSearch = 1,
            Categories = "general"
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8091")
        };

        var service = new SearXngWebSearchService(
            httpClient,
            options,
            excludedHostService.Object,
            NullLogger<SearXngWebSearchService>.Instance);

        var response = await service.SearchAsync("test", count: 2, skip: 1);

        response.Type.Should().Be("WebSearch");
        response.Web.Type.Should().Be("web_results");
        response.Web.Results.Should().HaveCount(2);
        response.Web.Results[0].Url.Should().Be("https://allowed.com/b");
        response.Web.Results[1].Url.Should().Be("https://allowed.com/c");
    }

    private static SearXngWebSearchService CreateService(HttpMessageHandler handler)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SearXngSearchOptions
        {
            BaseUrl = "http://localhost:8091",
            TimeoutMs = 15000,
            Language = "en-US",
            SafeSearch = 1,
            Categories = "general"
        });

        var excludedHostService = new Mock<IExcludedHostService>();
        excludedHostService
            .Setup(x => x.GetExcludedHostsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        excludedHostService
            .Setup(x => x.IsHostExcluded(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>()))
            .Returns(false);

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8091")
        };

        return new SearXngWebSearchService(
            httpClient,
            options,
            excludedHostService.Object,
            NullLogger<SearXngWebSearchService>.Instance);
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request, cancellationToken));
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 0)
                continue;

            if (!string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase))
                continue;

            return kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
        }

        return null;
    }
}
