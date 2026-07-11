using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaRuntimeContractTests
{
    [TestMethod]
    public async Task LoadModelAsync_WireBodyContainsAliasOnly()
    {
        string? body = null;
        var handler = new CapturingHandler(request =>
        {
            body = request.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8110/llama-cpp/") };
        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);
        await client.LoadModelAsync("qwen-alias");

        body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("model");
        doc.RootElement.GetProperty("model").GetString().Should().Be("qwen-alias");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
