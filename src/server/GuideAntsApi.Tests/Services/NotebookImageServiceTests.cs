using System.Net;
using System.Text;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class NotebookImageServiceTests
{
    private const string AzureProviderSection = "AzureOpenAiImages";
    private const string LocalProviderSection = "LocalServiceHosts:ImageGenerationBaseUrl";
    private const string GoogleProviderSection = "GoogleGeminiApi";
    private const string HuggingFaceProviderSection = "HuggingFace";
    private const string OpenRouterProviderSection = "OpenRouter";

    private static readonly object EnvLock = new();

    [TestMethod]
    public async Task GenerateImageAsync_UsesLocalProvider_WhenModeSelectsLocal()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OpenRouterImageSuccessPayload(), Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: LocalProviderSection,
            new Dictionary<string, string?>
            {
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8110",
                ["ImageGeneration:TimeoutSeconds"] = "60"
            });

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "local-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("http://localhost:8110/sd/txt2img");
    }

    [TestMethod]
    public async Task GenerateImageAsync_UsesCloudProvider_WhenModeSelectsAzure()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessPayload(), Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: AzureProviderSection,
            new Dictionary<string, string?>
            {
                ["AzureOpenAiImages:Endpoint"] = "https://images.example.com/",
                ["AzureOpenAiImages:ApiKey"] = "test-key",
                ["AzureOpenAiImages:Deployment"] = "flux.1-kontext-pro",
                ["AzureOpenAiImages:EditModelDeployment"] = "flux.1-kontext-pro",
                ["AzureOpenAiImages:ApiVersion"] = "2025-04-01-preview"
            });

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "cloud-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Contain("/openai/deployments/flux.1-kontext-pro/images/generations");
    }

    [TestMethod]
    public async Task GenerateImageAsync_ThrowsRoutingException_WhenModeReferencesUnsupportedProviderSection()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessPayload(), Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: "SomeBogusSection",
            new Dictionary<string, string?>());

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var act = async () => await service.GenerateImageAsync("a test image", "invalid-provider.png", context: context);

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
        ex.Which.ProviderSection.Should().Be("SomeBogusSection");
    }

    [TestMethod]
    public async Task GenerateImageAsync_UsesGoogleProvider_WhenModeSelectsGoogleGemini()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-image")) + "\"}}]}}]}",
                    Encoding.UTF8,
                    "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: GoogleProviderSection,
            new Dictionary<string, string?>
            {
                ["GoogleGeminiApi:ApiKey"] = "key"
            },
            modelId: "imagen-3.0-generate-002");

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "google-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/models/imagen-3.0-generate-002:generateContent");
        handler.LastRequestBody.Should().Contain("\"responseModalities\":[\"IMAGE\"]");
    }

    [TestMethod]
    public async Task GenerateImageAsync_UsesHuggingFaceProvider_WhenModeSelectsHuggingFace()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fake-image-binary"))
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: HuggingFaceProviderSection,
            new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token"
            },
            modelId: "stabilityai/stable-diffusion-xl-base-1.0");

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "hf-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Contain("api-inference.huggingface.co/models/");
    }

    [TestMethod]
    public async Task GenerateImageAsync_UsesOpenRouterProvider_WhenModeSelectsOpenRouter()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessPayload(), Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: OpenRouterProviderSection,
            new Dictionary<string, string?>
            {
                ["OpenRouter:ApiKey"] = "or-key",
                ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1"
            },
            modelId: "openai/gpt-image-1");

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "openrouter-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://openrouter.ai/api/v1/chat/completions");
        handler.LastRequestBody.Should().NotBeNullOrWhiteSpace();
        using var requestJson = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody);
        requestJson.RootElement.GetProperty("modalities")[0].GetString().Should().Be("image");
        requestJson.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be("a test image");
    }

    private static NotebookImageService CreateService(
        HttpClient httpClient,
        string providerSection,
        IDictionary<string, string?> values,
        string? modelId = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var resolver = modelId is null
            ? new FakeServiceModeResolver(
                RoutedServiceNames.ImageGeneration,
                providerSection: providerSection)
            : new FakeServiceModeResolver(
                (RoutedServiceNames.ImageGeneration, new ServiceMode(
                    ModeId: "default",
                    ProviderSection: providerSection,
                    ModelId: modelId,
                    RequestPresetJson: null,
                    Enabled: true,
                    IsDefault: true)));

        return new NotebookImageService(
            httpClientFactory.Object,
            configuration,
            NullLogger<NotebookImageService>.Instance,
            serviceProvider: null!,
            serviceModeResolver: resolver);
    }

    private static string SuccessPayload()
    {
        var bytes = Encoding.UTF8.GetBytes("fake-image-bytes");
        return $"{{\"data\":[{{\"b64_json\":\"{Convert.ToBase64String(bytes)}\"}}]}}";
    }

    private static string OpenRouterImageSuccessPayload()
    {
        var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-image-bytes"))}";
        return $$"""
        {
          "choices": [
            {
              "message": {
                "images": [
                  {
                    "image_url": {
                      "url": "{{dataUrl}}"
                    }
                  }
                ]
              }
            }
          ]
        }
        """;
    }

    private static IDisposable CreateEnvironmentScope()
    {
        lock (EnvLock)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "guideants-image-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var previousFileStorage = Environment.GetEnvironmentVariable("FileStorage__Path");
            var previousExecLogPath = Environment.GetEnvironmentVariable("DOCKER_EXEC_LOG_PATH");
            Environment.SetEnvironmentVariable("FileStorage__Path", tempRoot);
            Environment.SetEnvironmentVariable("DOCKER_EXEC_LOG_PATH", tempRoot);

            return new EnvironmentScope(tempRoot, previousFileStorage, previousExecLogPath);
        }
    }

    private sealed class EnvironmentScope(
        string tempRoot,
        string? previousFileStorage,
        string? previousExecLogPath) : IDisposable
    {
        public void Dispose()
        {
            lock (EnvLock)
            {
                Environment.SetEnvironmentVariable("FileStorage__Path", previousFileStorage);
                Environment.SetEnvironmentVariable("DOCKER_EXEC_LOG_PATH", previousExecLogPath);
            }

            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // ignored on cleanup
                }
            }
        }
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
