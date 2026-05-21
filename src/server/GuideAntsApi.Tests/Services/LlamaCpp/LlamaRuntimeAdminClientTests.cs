using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaRuntimeAdminClientTests
{
    [TestMethod]
    public async Task DeleteRouterEntryAsync_Sends_Delete_To_Router_Entries()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NoContent));

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8086/"),
        };

        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);

        var ok = await client.DeleteRouterEntryAsync("My-Alias_1");

        ok.Should().BeTrue();
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.ToString().Should().Be("http://localhost:8086/router/entries/My-Alias_1");
    }

    [TestMethod]
    public async Task DeleteRouterEntryAsync_Returns_False_On_404()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8086/"),
        };

        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);

        var ok = await client.DeleteRouterEntryAsync("missing");

        ok.Should().BeFalse();
    }

    [TestMethod]
    public async Task RestartLlamaServerAsync_PostsToLlamaRestart_AndParsesResult()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"restarted\":true,\"termed\":true,\"oldPid\":42,\"newPid\":43}",
                Encoding.UTF8,
                "application/json")
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8086/"),
        };

        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);

        var result = await client.RestartLlamaServerAsync();

        result.Restarted.Should().BeTrue();
        result.Termed.Should().BeTrue();
        result.OldPid.Should().Be(42);
        result.NewPid.Should().Be(43);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().Should().Be("http://localhost:8086/llama/restart");
    }

    [TestMethod]
    public async Task RestartLlamaServerAsync_Throws_TimeoutException_On_504()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
        {
            Content = new StringContent(
                "{\"detail\":\"llama-server did not respawn within 30s\"}",
                Encoding.UTF8,
                "application/json")
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8086/"),
        };

        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);

        var act = async () => await client.RestartLlamaServerAsync();

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [TestMethod]
    public async Task StartDownloadAsync_ThrowsConflictException_WithExistingOperation()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                "{\"detail\":{\"operationId\":\"op-existing\",\"status\":\"downloading\",\"routerModelId\":\"qwen-local\",\"progress\":0.4}}",
                Encoding.UTF8,
                "application/json")
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8086/"),
        };

        var client = new LlamaRuntimeAdminClient(httpClient, NullLogger<LlamaRuntimeAdminClient>.Instance);

        var request = new StartModelDownloadRequest(
            Repository: "unsloth/Qwen3.6-9B-GGUF",
            QuantIncludePattern: "*Q5_K_M*",
            MmprojIncludePattern: string.Empty,
            RouterModelId: "qwen-local",
            TargetDirectory: "qwen-local");

        var act = async () => await client.StartDownloadAsync(request, null, false, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<LlamaRuntimeAdminConflictException>();
        ex.Which.ExistingOperation.OperationId.Should().Be("op-existing");
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_responder(request));
        }
    }
}
