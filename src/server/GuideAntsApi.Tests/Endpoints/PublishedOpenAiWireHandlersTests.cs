using System.Text.Json;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Services.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GuideAntsApi.Tests.Endpoints;

[TestClass]
public sealed class PublishedOpenAiWireHandlersTests
{
    [TestMethod]
    public async Task GetModelsAsync_Returns_enabled_aliases_only()
    {
        var pubId = Guid.NewGuid();
        var context = CreateExecutionContext(
            pubId,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                AliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guide"] = "guide-alias",
                    ["embeddings"] = "embeddings-alias",
                    ["image"] = "image-alias"
                },
                EndpointFlags = new PublishedWireApiEndpointFlagsDto
                {
                    Models = true,
                    ChatCompletions = true,
                    Responses = true,
                    Embeddings = true,
                    ImageGenerations = false,
                    AudioTranscriptions = false,
                    AudioSpeech = false
                }
            });

        var resolver = new StubResolver(context);
        var http = new DefaultHttpContext();

        var result = await PublishedOpenAiWireHandlers.GetModelsAsync(http, pubId, resolver);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(executed.Body);
        var data = json.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(2);
        data.EnumerateArray().Select(x => x.GetProperty("id").GetString()).Should().BeEquivalentTo(
            ["embeddings-alias", "guide-alias"],
            options => options.WithoutStrictOrdering());
    }

    [TestMethod]
    public async Task PostChatCompletionsAsync_Returns_unsupported_feature_for_streaming()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiChatCompletionsRequest
        {
            Model = "guide",
            Stream = true,
            Messages = ParseJsonElement("[]")
        };

        var result = await PublishedOpenAiWireHandlers.PostChatCompletionsAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("unsupported_feature");
        error.GetProperty("param").GetString().Should().Be("stream");
    }

    [TestMethod]
    public async Task PostResponsesAsync_Returns_unsupported_feature_for_streaming()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var conversationService = new Mock<IPublishedConversationService>(MockBehavior.Strict);
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiResponsesRequest
        {
            Model = "guide",
            Stream = true,
            Input = ParseJsonElement("\"hello\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostResponsesAsync(
            http,
            pubId,
            request,
            resolver,
            conversationService.Object);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("unsupported_feature");
        error.GetProperty("param").GetString().Should().Be("stream");
    }

    [TestMethod]
    public async Task PostEmbeddingsAsync_Uses_service_mode_and_records_provider_metadata()
    {
        var pubId = Guid.NewGuid();
        var executionContext = CreateExecutionContext(pubId);
        var resolver = new StubResolver(executionContext);
        var embeddingService = new Mock<IEmbeddingService>();
        embeddingService
            .Setup(s => s.GetEmbeddingsAsync(
                It.IsAny<IEnumerable<string>>(),
                EmbeddingPurpose.Query,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([ [ 0.1f, 0.2f, 0.3f ] ]);

        var modeResolver = new Mock<IServiceModeResolver>();
        modeResolver
            .Setup(s => s.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceMode(
                ModeId: "emb-default",
                ProviderSection: "EmbeddingsSection",
                ModelId: "text-embedding-test",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true));

        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();
        var request = new PublishedOpenAiWireHandlers.OpenAiEmbeddingsRequest
        {
            Model = "embeddings",
            Input = ParseJsonElement("\"hello\"")
        };

        var result = await PublishedOpenAiWireHandlers.PostEmbeddingsAsync(
            http,
            pubId,
            request,
            resolver,
            embeddingService.Object,
            modeResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status200OK);
        modeResolver.Verify(
            s => s.ResolveAsync(RoutedServiceNames.Embeddings, null, It.IsAny<CancellationToken>()),
            Times.Once);
        usageRecorder.Calls.Should().ContainSingle();
        usageRecorder.Calls[0].ProviderServiceMode.Should().Be("emb-default");
        usageRecorder.Calls[0].ProviderModel.Should().Be("text-embedding-test");
        usageRecorder.Calls[0].Service.Should().Be("EmbeddingsSection");
    }

    [TestMethod]
    public async Task PostImageGenerationsAsync_Requires_prompt()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var imageService = new Mock<INotebookImageService>(MockBehavior.Strict);
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        var storagePathResolver = new Mock<IStoragePathResolver>(MockBehavior.Strict);
        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();

        var result = await PublishedOpenAiWireHandlers.PostImageGenerationsAsync(
            http,
            pubId,
            new PublishedOpenAiWireHandlers.OpenAiImageGenerationsRequest { Model = "image", Prompt = "" },
            resolver,
            imageService.Object,
            modeResolver.Object,
            storagePathResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_prompt");
    }

    [TestMethod]
    public async Task PostAudioTranscriptionsAsync_Requires_multipart_form_data()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var transcriptionService = new Mock<ISpeechTranscriptionService>(MockBehavior.Strict);
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json";

        var result = await PublishedOpenAiWireHandlers.PostAudioTranscriptionsAsync(
            http,
            pubId,
            resolver,
            transcriptionService.Object,
            modeResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_content_type");
    }

    [TestMethod]
    public async Task PostAudioSpeechAsync_Rejects_non_wav_response_format()
    {
        var pubId = Guid.NewGuid();
        var resolver = new StubResolver(CreateExecutionContext(pubId));
        var speechService = new Mock<ISpeechSynthesisService>(MockBehavior.Strict);
        var modeResolver = new Mock<IServiceModeResolver>(MockBehavior.Strict);
        var usageRecorder = new CapturingWireUsageRecorder();
        var http = new DefaultHttpContext();

        var result = await PublishedOpenAiWireHandlers.PostAudioSpeechAsync(
            http,
            pubId,
            new PublishedOpenAiWireHandlers.OpenAiAudioSpeechRequest
            {
                Model = "speech",
                Input = "hello",
                ResponseFormat = "mp3"
            },
            resolver,
            speechService.Object,
            modeResolver.Object,
            usageRecorder);
        var executed = await ExecuteResultAsync(result);

        executed.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(executed.Body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("unsupported_feature");
        error.GetProperty("param").GetString().Should().Be("response_format");
    }

    private static PublishedApiExecutionContext CreateExecutionContext(
        Guid pubId,
        PublishedWireApiConfigDto? wireApiConfig = null)
    {
        return new PublishedApiExecutionContext(
            PubId: pubId,
            ProjectId: Guid.NewGuid(),
            NotebookId: Guid.NewGuid(),
            GuideId: Guid.NewGuid(),
            PublishedGuide: new GuideAntsApi.DataModel.Models.PublishedGuide { Id = pubId, Active = true },
            WireApiConfig: wireApiConfig ?? new PublishedWireApiConfigDto { Enabled = true },
            AuthMode: PublishedApiAuthMode.Anonymous,
            ExternalUserIdentity: "user",
            InternalUserId: null,
            SourceChannel: PublishedApiExecutionContextResolver.WireApiSourceChannel,
            ExternalRequestId: "req-123",
            EndpointName: "models");
    }

    private static JsonElement ParseJsonElement(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return (httpContext.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class StubResolver(PublishedApiExecutionContext context) : IPublishedApiExecutionContextResolver
    {
        public Task<PublishedApiExecutionResolution> ResolveAsync(
            HttpContext httpContext,
            Guid pubId,
            string endpointName,
            int? endpointMaxBytes = null,
            CancellationToken ct = default)
        {
            var resolved = context with { EndpointName = endpointName };
            return Task.FromResult(PublishedApiExecutionResolution.Pass(resolved));
        }
    }

    private sealed class CapturingWireUsageRecorder : IPublishedWireUsageRecorder
    {
        public List<Call> Calls { get; } = [];

        public Task RecordAsync(
            PublishedApiExecutionContext context,
            GuideAnts.Usage.UsageCategory category,
            string service,
            string operation,
            UsageMetrics metrics,
            string endpoint,
            string status = "success",
            string? alias = null,
            string? providerModel = null,
            string? providerServiceMode = null,
            long? requestBytes = null,
            long? inputCount = null,
            long? outputCount = null,
            decimal costUsd = 0m,
            string? modelDeploymentId = null,
            CancellationToken ct = default)
        {
            Calls.Add(new Call(service, operation, endpoint, alias, providerModel, providerServiceMode, status));
            return Task.CompletedTask;
        }

        public sealed record Call(
            string Service,
            string Operation,
            string Endpoint,
            string? Alias,
            string? ProviderModel,
            string? ProviderServiceMode,
            string Status);
    }
}
