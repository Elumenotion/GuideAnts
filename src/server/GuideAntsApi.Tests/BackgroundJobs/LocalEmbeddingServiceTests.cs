using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class LocalEmbeddingServiceTests
{
    [TestMethod]
    public async Task GetEmbeddingsAsync_LocalQuery_PadsTo1536AndCallsEmbedEndpoint()
    {
        var sourceVector = Enumerable.Range(0, 1024).Select(x => (float)x / 1000f).ToArray();
        string? requestJson = null;
        Uri? requestUri = null;

        using var httpClient = new HttpClient(new DelegatingStubHandler(async (request, _) =>
        {
            requestUri = request.RequestUri;
            requestJson = await request.Content!.ReadAsStringAsync();
            var payload = JsonSerializer.Serialize(new
            {
                data = new[] { new { embedding = sourceVector } },
                dimensions = 1024,
                modelRef = "microsoft/harrier-oss-v1-0.6b"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }));

        var service = new LocalEmbeddingService(
            httpClient,
            new StaticOptionsMonitor<EmbeddingsOptions>(new EmbeddingsOptions
            {                TimeoutSeconds = 30
            }),
            new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions
            {
                EmbeddingsBaseUrl = "http://localhost:8110"
            }),
            NullLogger<LocalEmbeddingService>.Instance);

        var vectors = await service.GetEmbeddingsAsync(["find this"], EmbeddingPurpose.Query);

        requestUri.Should().NotBeNull();
        requestUri!.ToString().Should().Be("http://localhost:8110/emb/embed");
        requestJson.Should().Contain("\"purpose\":\"query\"");
        vectors.Should().HaveCount(1);
        vectors[0].Should().HaveCount(1536);
        vectors[0][0].Should().BeApproximately(sourceVector[0], 0.0001f);
        vectors[0][1023].Should().BeApproximately(sourceVector[1023], 0.0001f);
        vectors[0][1024].Should().Be(0f);
        vectors[0][1535].Should().Be(0f);
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_MalformedVector_Throws()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((_, _) =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                data = new[] { new { embedding = new[] { 0.1f, 0.2f } } },
                dimensions = 1024,
                modelRef = "microsoft/harrier-oss-v1-0.6b"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));

        var service = new LocalEmbeddingService(
            httpClient,
            new StaticOptionsMonitor<EmbeddingsOptions>(new EmbeddingsOptions
            {                TimeoutSeconds = 30
            }),
            new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions
            {
                EmbeddingsBaseUrl = "http://localhost:8110"
            }),
            NullLogger<LocalEmbeddingService>.Instance);

        var act = async () => await service.GetEmbeddingsAsync(["bad"], EmbeddingPurpose.Document);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected 1024*");
    }

    [TestMethod]
    public async Task ProviderRoutedEmbeddingService_UsesLocalProvider_WhenModeSelectsLocal()
    {
        var azureCalls = 0;
        var localCalls = 0;

        using var azureClient = new HttpClient(new DelegatingStubHandler((_, _) =>
        {
            azureCalls++;
            var payload = JsonSerializer.Serialize(new
            {
                data = new[] { new { embedding = Enumerable.Repeat(0.1f, 1536).ToArray() } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));

        using var localClient = new HttpClient(new DelegatingStubHandler((_, _) =>
        {
            localCalls++;
            var payload = JsonSerializer.Serialize(new
            {
                data = new[] { new { embedding = Enumerable.Repeat(0.1f, 1024).ToArray() } },
                dimensions = 1024,
                modelRef = "local"
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));

        var routed = new ProviderRoutedEmbeddingService(
            new AzureOpenAiEmbeddingService(
                azureClient,
                Microsoft.Extensions.Options.Options.Create(new AzureOpenAiEmbeddingOptions
                {
                    Endpoint = "https://example.com",
                    ApiKey = "key",
                    Deployment = "embed"
                })),
            new LocalEmbeddingService(
                localClient,
                new StaticOptionsMonitor<EmbeddingsOptions>(new EmbeddingsOptions
                {                    TimeoutSeconds = 30
                }),
                new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions
                {
                    EmbeddingsBaseUrl = "http://localhost:8110"
                }),
                NullLogger<LocalEmbeddingService>.Instance),
            new FakeServiceModeResolver(
                RoutedServiceNames.Embeddings,
                providerSection: "LocalServiceHosts:EmbeddingsBaseUrl"));

        await routed.GetEmbeddingsAsync(["hello"], EmbeddingPurpose.Document);

        localCalls.Should().Be(1);
        azureCalls.Should().Be(0);
    }

    [TestMethod]
    public async Task ProviderRoutedEmbeddingService_ThrowsRoutingException_ForUnsupportedProviderSection()
    {
        var routed = new ProviderRoutedEmbeddingService(
                new AzureOpenAiEmbeddingService(
                new HttpClient(new DelegatingStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
                Microsoft.Extensions.Options.Options.Create(new AzureOpenAiEmbeddingOptions())),
            new LocalEmbeddingService(
                new HttpClient(new DelegatingStubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
                new StaticOptionsMonitor<EmbeddingsOptions>(new EmbeddingsOptions()),
                new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions()),
                NullLogger<LocalEmbeddingService>.Instance),
            new FakeServiceModeResolver(
                RoutedServiceNames.Embeddings,
                providerSection: "SomeBogusSection"));

        var act = async () => await routed.GetEmbeddingsAsync(["hello"], EmbeddingPurpose.Document);
        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
        ex.Which.ProviderSection.Should().Be("SomeBogusSection");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_RespectsConfiguredTimeout()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));

        var service = new LocalEmbeddingService(
            httpClient,
            new StaticOptionsMonitor<EmbeddingsOptions>(new EmbeddingsOptions
            {                TimeoutSeconds = 1
            }),
            new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions
            {
                EmbeddingsBaseUrl = "http://localhost:8110"
            }),
            NullLogger<LocalEmbeddingService>.Instance);

        var act = async () => await service.GetEmbeddingsAsync(["slow"], EmbeddingPurpose.Document);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_LocalRequests_AreMeteredToSingleInFlightCall()
    {
        var sourceVector = Enumerable.Repeat(0.1f, 1024).ToArray();
        var inFlight = 0;
        var maxInFlight = 0;

        using var httpClient = new HttpClient(new DelegatingStubHandler(async (_, _) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            while (true)
            {
                var observed = Volatile.Read(ref maxInFlight);
                if (current <= observed)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref maxInFlight, current, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                var payload = JsonSerializer.Serialize(new
                {
                    data = new[] { new { embedding = sourceVector } },
                    dimensions = 1024,
                    modelRef = "local"
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        }));

        var service = new LocalEmbeddingService(
            httpClient,
            new StaticOptionsMonitor<EmbeddingsOptions>(new EmbeddingsOptions
            {                TimeoutSeconds = 30,
                LocalMinIntervalMs = 0
            }),
            new StaticOptionsMonitor<LocalServiceHostsOptions>(new LocalServiceHostsOptions
            {
                EmbeddingsBaseUrl = "http://localhost:8110"
            }),
            NullLogger<LocalEmbeddingService>.Instance);

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            service.GetEmbeddingsAsync(["a"], EmbeddingPurpose.Document),
            service.GetEmbeddingsAsync(["b"], EmbeddingPurpose.Document));
        sw.Stop();

        maxInFlight.Should().Be(1);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(250);
    }

    private sealed class StaticOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue => value;
        public TOptions Get(string? name) => value;
        public IDisposable OnChange(Action<TOptions, string?> listener) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose()
        {
        }
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
