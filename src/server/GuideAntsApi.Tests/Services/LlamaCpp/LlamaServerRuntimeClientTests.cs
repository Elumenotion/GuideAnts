using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaServerRuntimeClientTests
{
    [TestMethod]
    public async Task ListModelsAsync_PreservesBasePathPrefix()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        await client.ListModelsAsync();

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("http://localhost:8110/llama-cpp/models");
    }

    [TestMethod]
    public async Task ListModelsAsync_PreservesBasePathPrefix_WhenBaseAddressHasNoTrailingSlash()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        await client.ListModelsAsync();

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("http://localhost:8110/llama-cpp/models");
    }

    [TestMethod]
    public async Task LoadModelAsync_PreservesBasePathPrefix()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        await client.LoadModelAsync("qwen");

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("http://localhost:8110/llama-cpp/models/load");
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_responder(request));
        }
    }
}
