using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.Configuration;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiRuntimeAlignmentVerifierTests
{
    [TestMethod]
    public void IdentifiersMatch_MatchesCatalogAliasToGgufFilename()
    {
        var expected = LocalAiRuntimeAlignmentVerifier.CollectRuntimeIdentifiers("qwen3_embedding_0_6b");
        var actual = LocalAiRuntimeAlignmentVerifier.CollectRuntimeIdentifiers(
            "/models/qwen3-embedding-0.6b/Qwen3-Embedding-0.6B-Q8_0.gguf");

        LocalAiRuntimeAlignmentVerifier.IdentifiersMatch(expected, actual).Should().BeTrue();
    }

    [TestMethod]
    public void IdentifiersMatch_IsCaseInsensitive_ForNonPathIdentifiers()
    {
        var expected = LocalAiRuntimeAlignmentVerifier.CollectRuntimeIdentifiers("MyAlias");
        var actual = LocalAiRuntimeAlignmentVerifier.CollectRuntimeIdentifiers("myalias");

        LocalAiRuntimeAlignmentVerifier.IdentifiersMatch(expected, actual).Should().BeTrue();
    }

    [TestMethod]
    public void ResolvePlanRefs_CollectsAllAuxiliaryIdentifiers()
    {
        var section = new JsonObject
        {
            ["modelPath"] = "/models/foo/model.gguf",
            ["modelId"] = "foo-model",
            ["catalogEntryId"] = "foo-catalog",
            ["bundleId"] = "foo-bundle"
        };

        var refs = LocalAiRuntimeAlignmentVerifier.ResolvePlanRefs(RoutedServiceNames.Embeddings, section);

        refs.Should().Contain(new[]
        {
            "/models/foo/model.gguf",
            "foo-model",
            "foo-catalog",
            "foo-bundle"
        });
    }

    [TestMethod]
    public async Task FindMismatchesAsync_WaitsForWarmupIncompleteThenSucceeds()
    {
        var attempts = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"loaded":false,"failed":false,"status":"warmup-incomplete"}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"loaded":true,"catalogEntryId":"qwen3_embedding_0_6b"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var verifier = CreateVerifier(handler, llamaModels: Array.Empty<LlamaModelData>());
        var plan = """
                   {
                     "services": {
                       "Embeddings": {
                         "enabled": true,
                         "catalogEntryId": "qwen3_embedding_0_6b"
                       }
                     }
                   }
                   """;

        var mismatches = await verifier.FindMismatchesAsync(plan, CancellationToken.None);

        mismatches.Should().BeEmpty();
        attempts.Should().BeGreaterThan(1);
    }

    [TestMethod]
    public async Task FindMismatchesAsync_ReportsMismatch_WhenIdentifiersNeverAlign()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"loaded":true,"modelRef":"other-model"}""",
                Encoding.UTF8,
                "application/json")
        });

        var verifier = CreateVerifier(handler, llamaModels: Array.Empty<LlamaModelData>());
        var plan = """
                   {
                     "services": {
                       "Embeddings": {
                         "enabled": true,
                         "catalogEntryId": "qwen3_embedding_0_6b"
                       }
                     }
                   }
                   """;

        var mismatches = await verifier.FindMismatchesAsync(plan, CancellationToken.None);

        mismatches.Should().ContainSingle(m => m.ServiceId == RoutedServiceNames.Embeddings);
    }

    private static LocalAiRuntimeAlignmentVerifier CreateVerifier(
        HttpMessageHandler handler,
        IReadOnlyList<LlamaModelData> llamaModels)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://localhost:8080",
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var httpClientFactory = new SingleHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080")
        });

        var llamaClient = new StubLlamaRuntimeClient(llamaModels);
        var stackResolver = new LocalAiStackHostResolver(configuration);

        return new LocalAiRuntimeAlignmentVerifier(
            httpClientFactory,
            configuration,
            stackResolver,
            llamaClient,
            NullLogger<LocalAiRuntimeAlignmentVerifier>.Instance);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubLlamaRuntimeClient(IReadOnlyList<LlamaModelData> models) : ILlamaServerRuntimeClient
    {
        public Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlamaModelsResponse { Data = models.ToList() });

        public Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlamaOpenAiModelsResponse());

        public Task LoadModelAsync(string modelPathOrPreset, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnloadModelAsync(string routerModelId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
