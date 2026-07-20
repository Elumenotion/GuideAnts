using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class BundleDefinitionProjectionServiceTests
{
    [TestMethod]
    public async Task MigrateLegacyBundleFoldersAsync_DoesNotThrowWhenMigrationEndpointReturns405()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8080",
            })
            .Build();

        var service = new BundleDefinitionProjectionService(
            settingsService: null!,
            new StubHttpClientFactory(new Dictionary<string, HttpResponseMessage>(StringComparer.OrdinalIgnoreCase)
            {
                ["migrate-folder"] = new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                {
                    Content = new StringContent("{\"detail\":\"Method Not Allowed\"}", Encoding.UTF8, "application/json"),
                },
            }),
            configuration,
            NullLogger<BundleDefinitionProjectionService>.Instance);

        var act = async () => await service.MigrateLegacyBundleFoldersAsync();

        await act.Should().NotThrowAsync();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, HttpResponseMessage> _responses;

        public StubHttpClientFactory(Dictionary<string, HttpResponseMessage> responses) =>
            _responses = responses;

        public HttpClient CreateClient(string name) =>
            new(new StubHttpMessageHandler(_responses));

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, HttpResponseMessage> _responses;

            public StubHttpMessageHandler(Dictionary<string, HttpResponseMessage> responses) =>
                _responses = responses;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri?.ToString() ?? string.Empty;
                foreach (var (key, response) in _responses)
                {
                    if (uri.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(CloneResponse(response));
                    }
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage CloneResponse(HttpResponseMessage template)
            {
                var clone = new HttpResponseMessage(template.StatusCode);
                if (template.Content is not null)
                {
                    var body = template.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    clone.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                return clone;
            }
        }
    }
}
