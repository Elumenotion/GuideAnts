using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class HuggingFaceEmbeddingServiceTests
{
    [TestMethod]
    public async Task GetEmbeddingsAsync_HuggingFace_ParsesSingleVectorResponse()
    {
        string? requestBody = null;
        using var httpClient = new HttpClient(new DelegatingStubHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return BuildHuggingFaceProviderMappingResponse("hf-inference", "microsoft/harrier-oss-v1-0.6b", "feature-extraction");
            }

            requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            request.RequestUri!.ToString().Should().Contain("router.huggingface.co/hf-inference/models/microsoft/harrier-oss-v1-0.6b");
            var payload = JsonSerializer.Serialize(new[] { 0.1, 0.2, 0.3 });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }));

        var service = new HuggingFaceEmbeddingService(httpClient, BuildConfiguration());
        var result = await service.GetEmbeddingsAsync(
            ["hello"],
            "microsoft/harrier-oss-v1-0.6b",
            requestPresetJson: null);

        result.Should().HaveCount(1);
        result[0].Should().Equal(0.1f, 0.2f, 0.3f);
        requestBody.Should().Contain("\"inputs\":\"hello\"");
        requestBody.Should().NotContain("\"Inputs\"");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_HuggingFace_ParsesBatchVectorResponse()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((request, _) =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return Task.FromResult(BuildHuggingFaceProviderMappingResponse("hf-inference", "microsoft/harrier-oss-v1-0.6b", "feature-extraction"));
            }

            var payload = JsonSerializer.Serialize(new[]
            {
                new[] { 0.1, 0.2 },
                new[] { 0.3, 0.4 },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));

        var service = new HuggingFaceEmbeddingService(httpClient, BuildConfiguration());
        var result = await service.GetEmbeddingsAsync(
            ["one", "two"],
            "microsoft/harrier-oss-v1-0.6b",
            requestPresetJson: null);

        result.Should().HaveCount(2);
        result[0].Should().Equal(0.1f, 0.2f);
        result[1].Should().Equal(0.3f, 0.4f);
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_HuggingFace_ThrowsForHttpErrorWithStatusAndBody()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((request, _) =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return Task.FromResult(BuildHuggingFaceProviderMappingResponse("hf-inference", "microsoft/harrier-oss-v1-0.6b", "feature-extraction"));
            }

            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":\"rate limit\"}", Encoding.UTF8, "application/json")
            });
        }));

        var service = new HuggingFaceEmbeddingService(httpClient, BuildConfiguration());
        var act = async () => await service.GetEmbeddingsAsync(
            ["hello"],
            "microsoft/harrier-oss-v1-0.6b",
            requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*request failed (429)*rate limit*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_HuggingFace_ThrowsForMalformedResponseShape()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((request, _) =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return Task.FromResult(BuildHuggingFaceProviderMappingResponse("hf-inference", "microsoft/harrier-oss-v1-0.6b", "feature-extraction"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json")
            });
        }));

        var service = new HuggingFaceEmbeddingService(httpClient, BuildConfiguration());
        var act = async () => await service.GetEmbeddingsAsync(
            ["hello"],
            "microsoft/harrier-oss-v1-0.6b",
            requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*response shape was not recognized*");
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_HuggingFace_AllowsHarrierWithoutPresetAllowlist()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((request, _) =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return Task.FromResult(BuildHuggingFaceProviderMappingResponse("hf-inference", "microsoft/harrier-oss-v1-0.6b", "feature-extraction"));
            }

            var payload = JsonSerializer.Serialize(new[] { 0.1, 0.2, 0.3 });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));

        var service = new HuggingFaceEmbeddingService(httpClient, BuildConfiguration());
        var result = await service.GetEmbeddingsAsync(
            ["hello"],
            "microsoft/harrier-oss-v1-0.6b",
            requestPresetJson: null);

        result.Should().HaveCount(1);
        result[0].Should().Equal(0.1f, 0.2f, 0.3f);
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_HuggingFace_RejectsSentenceSimilarityOnlyMappings()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((request, _) =>
        {
            if (request.RequestUri?.Host == "huggingface.co")
            {
                return Task.FromResult(BuildHuggingFaceProviderMappingResponse(
                    "hf-inference",
                    "sentence-transformers/all-MiniLM-L6-v2",
                    "sentence-similarity"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        }));

        var service = new HuggingFaceEmbeddingService(httpClient, BuildConfiguration());
        var act = async () => await service.GetEmbeddingsAsync(
            ["hello"],
            "sentence-transformers/all-MiniLM-L6-v2",
            requestPresetJson: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No live Hugging Face feature-extraction route found*sentence-similarity*");
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token",
                ["HuggingFace:RouterBaseUrl"] = "https://router.huggingface.co/v1",
            })
            .Build();
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private static HttpResponseMessage BuildHuggingFaceProviderMappingResponse(string provider, string providerId, string task)
    {
        var payload = $$"""
        {
          "inferenceProviderMapping": {
            "{{provider}}": {
              "status": "live",
              "providerId": "{{providerId}}",
              "task": "{{task}}"
            }
          }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }
}
