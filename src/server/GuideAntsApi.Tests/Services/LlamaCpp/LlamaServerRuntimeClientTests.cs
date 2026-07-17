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

    [TestMethod]
    public async Task LoadModelAsync_AlreadyRunning_ReturnsWithoutError()
    {
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                ReasonPhrase = "Bad Request",
                Content = new StringContent(
                    "{\"error\":{\"code\":400,\"message\":\"model is already running\",\"type\":\"invalid_request_error\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        await client.LoadModelAsync("Qwen3.6-35B-A3B-MTP-GGUF");

        calls.Should().Be(1);
    }

    [TestMethod]
    public void IsBenignLoadConflict_MatchesAlreadyRunningOnModelsLoad()
    {
        LlamaServerRuntimeClient.IsBenignLoadConflict(
                "models/load",
                HttpStatusCode.BadRequest,
                """{"error":{"message":"model is already running"}}""")
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task LoadModelAsync_FailureMessageIncludesResponseBody()
    {
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "Internal Server Error",
                Content = new StringContent("instance name=gemma exited with status 1", Encoding.UTF8, "text/plain")
            };
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        var act = async () => await client.LoadModelAsync("gemma");

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        ex.Which.Message.Should().Contain("models/load");
        ex.Which.Message.Should().Contain("instance name=gemma exited with status 1");
        calls.Should().Be(1);
    }

    [TestMethod]
    public async Task ListModelsAsync_DeserializesRouterFailureFields()
    {
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"gemma\",\"status\":{\"value\":\"unloaded\"},\"failed\":true,\"exit_code\":1}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        var response = await client.ListModelsAsync();

        response.Data.Should().ContainSingle();
        response.Data[0].Failed.Should().BeTrue();
        response.Data[0].ExitCode.Should().Be(1);
    }

    [TestMethod]
    public async Task ListModelsAsync_RetriesTransientGatewayFailure()
    {
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    ReasonPhrase = "Bad Gateway",
                    Content = new StringContent("router is starting", Encoding.UTF8, "text/plain")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"qwen\",\"status\":{\"value\":\"loaded\"}}]}", Encoding.UTF8, "application/json")
                };
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        var response = await client.ListModelsAsync();

        calls.Should().Be(2);
        response.Data.Should().ContainSingle(m => m.Id == "qwen");
    }

    [TestMethod]
    public async Task ListModelsAsync_RetriesTransientConnectionFailure()
    {
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("Connection refused (guideants-ai:80)");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        await client.ListModelsAsync();

        calls.Should().Be(2);
    }

    [TestMethod]
    public void IsNonRetryableConnectionFailure_MatchesDnsResolutionFailure()
    {
        var ex = new HttpRequestException(
            "Name or service not known (guideants-ai:80)",
            new HttpRequestException("Name or service not known"));

        LlamaServerRuntimeClient.IsNonRetryableConnectionFailure(ex).Should().BeTrue();
    }

    [TestMethod]
    public async Task ListModelsAsync_DoesNotRetryDnsResolutionFailure()
    {
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            throw new HttpRequestException("Name or service not known (guideants-ai:80)");
        });

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8110/llama-cpp/")
        };

        var client = new LlamaServerRuntimeClient(httpClient, NullLogger<LlamaServerRuntimeClient>.Instance);

        var act = async () => await client.ListModelsAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1);
    }

    [TestMethod]
    public void MapRuntimeState_PrefersRouterFailedFlagOverUnloadedStatus()
    {
        var state = LlamaRuntimeInventoryService.MapRuntimeState(new LlamaModelData
        {
            Failed = true,
            ExitCode = 1,
            Status = new LlamaModelStatus { Value = "unloaded" }
        });

        state.Should().Be("failed");
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
