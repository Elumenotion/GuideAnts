using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class GoogleGeminiEmbeddingServiceTests
{
    [TestMethod]
    public async Task GetEmbeddingsAsync_Requests1536Dimensions_AndCamelCasePayload()
    {
        string? requestJson = null;
        Uri? requestUri = null;

        using var httpClient = new HttpClient(new DelegatingStubHandler(async (request, _) =>
        {
            requestUri = request.RequestUri;
            requestJson = await request.Content!.ReadAsStringAsync();

            var payload = JsonSerializer.Serialize(new
            {
                embeddings = new[]
                {
                    new
                    {
                        values = Enumerable.Repeat(0.25f, 1536).ToArray()
                    }
                }
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }));

        var service = new GoogleGeminiEmbeddingService(httpClient, BuildConfiguration());

        var vectors = await service.GetEmbeddingsAsync(["find this"], "gemini-embedding-001");

        requestUri.Should().NotBeNull();
        requestUri!.ToString().Should().Be(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents");
        requestJson.Should().Contain("\"requests\"");
        requestJson.Should().Contain("\"text\":\"find this\"");
        requestJson.Should().Contain("\"taskType\":\"RETRIEVAL_DOCUMENT\"");
        requestJson.Should().Contain("\"outputDimensionality\":1536");
        vectors.Should().HaveCount(1);
        vectors[0].Should().HaveCount(1536);
    }

    [TestMethod]
    public async Task GetEmbeddingsAsync_ThrowsWhenProviderReturnsWrongVectorSize()
    {
        using var httpClient = new HttpClient(new DelegatingStubHandler((_, _) =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                embeddings = new[]
                {
                    new
                    {
                        values = Enumerable.Repeat(0.25f, 3072).ToArray()
                    }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));

        var service = new GoogleGeminiEmbeddingService(httpClient, BuildConfiguration());

        var act = async () => await service.GetEmbeddingsAsync(["find this"], "gemini-embedding-001");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected 1536*");
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoogleGeminiApi:ApiKey"] = "test-api-key"
            })
            .Build();
    }
}
