using System.Net;
using System.Text;
using AntRunner.ToolCalling;
using FluentAssertions;
using GuideAnts.Usage;
using GuideAntsApi.Options;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
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
                ["AzureOpenAiImages:ApiVersion"] = "2025-04-01-preview"
            },
            modelId: "flux.1-kontext-pro");

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
    public async Task GenerateImageAsync_RecordsUsageWithSelectedProvider()
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

        var usageRecorder = new Mock<IUsageRecorder>();
        usageRecorder
            .Setup(recorder => recorder.RecordAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<UsageCategory>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<UsageMetrics>(),
                It.IsAny<decimal>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Guid?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var syncService = new Mock<INotebookFileSyncService>();
        syncService.Setup(service => service.SyncNotebookAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        var fileService = new Mock<INotebookFileService>();

        var services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options =>
                options.UseInMemoryDatabase($"image-usage-{Guid.NewGuid():N}"))
            .AddSingleton(usageRecorder.Object)
            .AddSingleton(syncService.Object)
            .AddSingleton(fileService.Object)
            .BuildServiceProvider();

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: GoogleProviderSection,
            new Dictionary<string, string?>
            {
                ["GoogleGeminiApi:ApiKey"] = "key"
            },
            modelId: "imagen-3.0-generate-002",
            serviceProvider: services);

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "google-usage-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        usageRecorder.Verify(recorder => recorder.RecordAsync(
            context.ProjectId,
            context.NotebookId,
            UsageCategory.ImageGeneration,
            ServiceProviderIds.ImageGenerationGoogleImagen,
            "image-generation",
            It.Is<UsageMetrics>(metrics => metrics.ValueOther > 0),
            It.IsAny<decimal>(),
            context.ConversationId,
            null,
            null,
            null,
            It.Is<string?>(metadata => metadata != null && metadata.Contains("google-usage-provider.png")),
            context.AssistantId,
            context.CurrentInvocationId,
            null,
            It.IsAny<CancellationToken>(),
            It.IsAny<Guid?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()),
            Times.Once);
    }

    [TestMethod]
    public async Task GenerateImageAsync_UsesHuggingFaceProvider_WhenModeSelectsHuggingFace()
    {
        using var scope = CreateEnvironmentScope();
        var fakeImageBytes = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = new CapturingHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        HfProviderMappingPayload("replicate", "stability-ai/sdxl"),
                        Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("router.huggingface.co", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"output\":[\"https://cdn.replicate.com/image.png\"]}",
                        Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fakeImageBytes)
            };
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: HuggingFaceProviderSection,
            new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token"
            },
            modelId: "stabilityai/stable-diffusion-xl-base-1.0-rate-limit");

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "hf-provider.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        handler.RequestUris.Should().Contain(uri => uri.Contains("/api/models/stabilityai/stable-diffusion-xl-base-1.0", StringComparison.Ordinal));
        handler.RequestUris.Should().Contain(uri => uri.Contains("router.huggingface.co/replicate/", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GenerateImageAsync_HuggingFace_ThrowsWithHttpStatusAndBody_OnRateLimit()
    {
        using var scope = CreateEnvironmentScope();
        var handler = new CapturingHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        HfProviderMappingPayload("replicate", "stability-ai/sdxl"),
                        Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":\"too many requests\"}", Encoding.UTF8, "application/json")
            };
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
        var result = await service.GenerateImageAsync("a test image", "hf-provider-rate-limit.png", context: context);

        result.StandardError.Should().Contain("Hugging Face image generation failed: 429");
        result.StandardError.Should().Contain("too many requests");
    }

    [TestMethod]
    public async Task GenerateImageAsync_HuggingFace_UsesProviderSpecificRequestBody()
    {
        using var scope = CreateEnvironmentScope();
        var fakeImageBytes = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = new CapturingHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        HfProviderMappingPayload("replicate", "tongyi-mai/z-image-turbo"),
                        Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("router.huggingface.co", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"output\":[\"https://cdn.replicate.com/image.png\"]}",
                        Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fakeImageBytes) };
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: HuggingFaceProviderSection,
            new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token"
            },
            modelId: "Tongyi-MAI/Z-Image-Turbo");

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "hf-provider-params.png", size: "1792x1024", n: 2, outputFormat: "jpeg", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        // Replicate wraps inputs under "input"; body must carry prompt, width, height — NOT the old OpenAI format
        handler.RequestBodies.Should().Contain(body => body.Contains("\"prompt\":\"a test image\""));
        handler.RequestBodies.Should().Contain(body => body.Contains("\"width\":1792"));
        handler.RequestBodies.Should().Contain(body => body.Contains("\"height\":1024"));
        handler.RequestBodies.Should().NotContain(body => body.Contains("\"model\":"));
        handler.RequestBodies.Should().NotContain(body => body.Contains("\"response_format\":"));
        handler.RequestBodies.Should().NotContain(body => body.Contains("\"size\":"));
        handler.RequestBodies.Should().NotContain(body => body.Contains("\"n\":", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GenerateImageAsync_HuggingFace_CallsModelMappingLookup_AndUsesResolvedProvider()
    {
        using var scope = CreateEnvironmentScope();
        var fakeImageBytes = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = new CapturingHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        HfProviderMappingPayload("replicate", "tongyi-mai/z-image-turbo"),
                        Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("router.huggingface.co", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"output\":[\"https://cdn.replicate.com/image.png\"]}",
                        Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fakeImageBytes) };
        });

        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            providerSection: HuggingFaceProviderSection,
            new Dictionary<string, string?>
            {
                ["HuggingFace:Token"] = "hf-token"
            },
            modelId: "Tongyi-MAI/Z-Image-Turbo");

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var result = await service.GenerateImageAsync("a test image", "hf-segment-encoded-path.png", context: context);

        result.StandardError.Should().BeNullOrEmpty();
        // Provider resolution lookup must be called
        handler.RequestUris.Should().Contain(uri => uri.Contains("/api/models/Tongyi-MAI/Z-Image-Turbo", StringComparison.Ordinal));
        // Router call must use the resolved provider path, not a hardcoded model path
        handler.RequestUris.Should().Contain(uri =>
            uri.Contains("router.huggingface.co/replicate/", StringComparison.Ordinal) &&
            uri.Contains("tongyi-mai/z-image-turbo", StringComparison.OrdinalIgnoreCase));
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
        string? modelId = null,
        string? requestPresetJson = null,
        IServiceProvider? serviceProvider = null)
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
                    RequestPresetJson: requestPresetJson,
                    Enabled: true,
                    IsDefault: true)));

        return new NotebookImageService(
            httpClientFactory.Object,
            configuration,
            NullLogger<NotebookImageService>.Instance,
            serviceProvider: serviceProvider!,
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

    private static string HfProviderMappingPayload(string provider, string providerId, string task = "text-to-image") =>
        "{\"inferenceProviderMapping\":{\"" + provider + "\":{\"status\":\"live\",\"task\":\"" + task + "\",\"providerId\":\"" + providerId + "\"}}}";

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
        public List<string> RequestUris { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.RequestUri is not null)
            {
                RequestUris.Add(request.RequestUri.ToString());
            }
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(LastRequestBody);
            return _responder(request);
        }
    }
}
