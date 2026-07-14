using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiStartupWarmupServiceTests
{
    [TestMethod]
    public async Task SyncDesiredAndApplyAsync_WritesIniAndApplies()
    {
        string? capturedIni = null;
        var orchestration = new Mock<ILocalAiWarmupOrchestrationClient>(MockBehavior.Strict);
        orchestration
            .Setup(c => c.PutDesiredAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((ini, _, _) => capturedIni = ini)
            .ReturnsAsync(new WarmupDesiredWriteResult(Revision: 3, Sha256: "abc", Changed: true));
        orchestration
            .Setup(c => c.ApplyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupApplyResult(
                Ok: true,
                Noop: false,
                Continue: false,
                Started: true,
                DesiredRevision: 3,
                AppliedRevision: 0,
                ApplyStatus: "applying"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.Embeddings, new ServiceMode(
                ModeId: "default",
                ProviderSection: "LocalServiceHosts:EmbeddingsBaseUrl",
                ModelId: "qwen3_embedding_0_6b",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(),
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(),
            builder,
            orchestration.Object,
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiStartupWarmupService>.Instance);

        await service.SyncDesiredAndApplyAsync(waitForCompletion: false);

        capturedIni.Should().NotBeNullOrWhiteSpace();
        capturedIni.Should().Contain("model_path = qwen3_embedding_0_6b");
        orchestration.VerifyAll();
    }

    [TestMethod]
    public async Task ReconcileLocalServiceAsync_NotActiveProvider_ReturnsWithoutOrchestration()
    {
        var orchestration = new Mock<ILocalAiWarmupOrchestrationClient>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var modeResolver = new FakeServiceModeResolver(
            (RoutedServiceNames.SpeechTranscription, new ServiceMode(
                ModeId: "default",
                ProviderSection: "SpeechTranscription.Azure",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true)));

        var builder = new Mock<ILocalAiDesiredStateBuilder>(MockBehavior.Strict);
        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(),
            builder.Object,
            orchestration.Object,
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiStartupWarmupService>.Instance);

        var result = await service.ReconcileLocalServiceAsync(
            RoutedServiceNames.SpeechTranscription,
            requestedModelRef: "qwen3_asr_0_6b");

        result.Outcome.Should().Be(LocalServiceReconcileOutcome.NotActiveProvider);
    }

    [TestMethod]
    public async Task ReconcileLocalServiceAsync_PersistsFolderRefVerbatim()
    {
        string? capturedIni = null;
        var orchestration = new Mock<ILocalAiWarmupOrchestrationClient>(MockBehavior.Strict);
        orchestration
            .Setup(c => c.PutDesiredAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, int?, CancellationToken>((ini, _, _) => capturedIni = ini)
            .ReturnsAsync(new WarmupDesiredWriteResult(Revision: 2, Sha256: "abc", Changed: true));
        orchestration
            .Setup(c => c.ApplyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupApplyResult(
                Ok: true,
                Noop: false,
                Continue: false,
                Started: true,
                DesiredRevision: 2,
                AppliedRevision: 0,
                ApplyStatus: "applying"));
        orchestration
            .Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupStatusDocument(
                SchemaVersion: 1,
                DesiredRevision: 2,
                AppliedRevision: 2,
                InProgressRevision: null,
                ApplyStatus: "applied",
                ApplyError: null,
                DesiredSha256: "abc",
                WrittenAt: "2026-07-12T19:00:00Z",
                Services: new Dictionary<string, WarmupServiceStatus>(StringComparer.Ordinal)
                {
                    [RoutedServiceNames.SpeechSynthesis] = new WarmupServiceStatus(
                        Desired: "on",
                        Applied: "on",
                        Phase: "ready",
                        Error: null,
                        PlanRef: "OmniVoice",
                        RouterAlias: null,
                        ModelId: "OmniVoice",
                        BundleId: null),
                }));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var routingGate = new RoutingActivationGate();
        routingGate.Activate();
        var modeResolver = new GatedServiceModeResolver(
            routingGate,
            RoutedServiceNames.SpeechSynthesis,
            new ServiceMode(
                ModeId: "local",
                ProviderSection: "LocalServiceHosts:SpeechSynthesisBaseUrl",
                ModelId: "chatterbox",
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true));

        var settingsMock = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settingsMock
            .Setup(s => s.SetServiceModeModelIdAsync(
                RoutedServiceNames.SpeechSynthesis,
                "OmniVoice",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => modeResolver.SetModelId("OmniVoice"));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(settingsMock.Object),
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(settingsMock.Object),
            builder,
            orchestration.Object,
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiStartupWarmupService>.Instance);

        var result = await service.ReconcileLocalServiceAsync(
            RoutedServiceNames.SpeechSynthesis,
            requestedModelRef: "OmniVoice");

        result.Outcome.Should().Be(LocalServiceReconcileOutcome.Warm);
        capturedIni.Should().Contain("model_path = OmniVoice");
        settingsMock.VerifyAll();
    }

    [TestMethod]
    public async Task ReconcileLocalServiceAsync_SelectActive_AutoActivatesLocalProvider()
    {
        var orchestration = new Mock<ILocalAiWarmupOrchestrationClient>(MockBehavior.Strict);
        orchestration
            .Setup(c => c.PutDesiredAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupDesiredWriteResult(Revision: 2, Sha256: "abc", Changed: true));
        orchestration
            .Setup(c => c.ApplyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupApplyResult(
                Ok: true,
                Noop: false,
                Continue: false,
                Started: true,
                DesiredRevision: 2,
                AppliedRevision: 0,
                ApplyStatus: "applying"));
        orchestration
            .Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WarmupStatusDocument(
                SchemaVersion: 1,
                DesiredRevision: 2,
                AppliedRevision: 2,
                InProgressRevision: null,
                ApplyStatus: "applied",
                ApplyError: null,
                DesiredSha256: "abc",
                WrittenAt: "2026-07-12T19:00:00Z",
                Services: new Dictionary<string, WarmupServiceStatus>(StringComparer.Ordinal)
                {
                    [ImageGenerationOptions.SectionName] = new WarmupServiceStatus(
                        Desired: "on",
                        Applied: "on",
                        Phase: "ready",
                        Error: null,
                        PlanRef: "flux2-klein-4b-q4ks",
                        RouterAlias: null,
                        ModelId: null,
                        BundleId: "flux2-klein-4b-q4ks"),
                }));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://localhost:8080/llama-cpp",
            })
            .Build();

        var routingGate = new RoutingActivationGate();
        var modeResolver = new GatedServiceModeResolver(
            routingGate,
            ImageGenerationOptions.SectionName,
            new ServiceMode(
                ModeId: "local",
                ProviderSection: "LocalServiceHosts:ImageGenerationBaseUrl",
                ModelId: null,
                RequestPresetJson: null,
                Enabled: true,
                IsDefault: true));

        var settingsMock = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settingsMock
            .Setup(s => s.EnsureServiceModeExistsAsync(
                ImageGenerationOptions.SectionName,
                ServiceProviderIds.ImageGenerationLocalSdHttp,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => routingGate.Activate());
        settingsMock
            .Setup(s => s.SetServiceActiveProviderAsync(
                ImageGenerationOptions.SectionName,
                ServiceProviderIds.ImageGenerationLocalSdHttp,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceEditorStateDto(
                ServiceId: ImageGenerationOptions.SectionName,
                ActiveProviderId: ServiceProviderIds.ImageGenerationLocalSdHttp,
                Providers: [],
                Readiness: new ServiceEditorReadinessDto("ready", [], [])))
            .Callback(() => routingGate.Activate());
        settingsMock
            .Setup(s => s.SetServiceModeModelIdAsync(
                ImageGenerationOptions.SectionName,
                "flux2-klein-4b-q4ks",
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => modeResolver.SetModelId("flux2-klein-4b-q4ks"));

        var builder = new LocalAiDesiredStateBuilder(
            configuration,
            new ServiceScopeFactoryStub(settingsMock.Object),
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiDesiredStateBuilder>.Instance);

        var service = new LocalAiStartupWarmupService(
            configuration,
            new ServiceScopeFactoryStub(settingsMock.Object),
            builder,
            orchestration.Object,
            modeResolver,
            CreateHttpClientFactory(),
            NullLogger<LocalAiStartupWarmupService>.Instance);

        var result = await service.ReconcileLocalServiceAsync(
            ImageGenerationOptions.SectionName,
            requestedModelRef: "flux2-klein-4b-q4ks");

        result.Outcome.Should().Be(LocalServiceReconcileOutcome.Warm);
        settingsMock.VerifyAll();
        orchestration.VerifyAll();
    }

    private sealed class ServiceScopeFactoryStub : IServiceScopeFactory
    {
        private readonly IApplicationSettingsService? _settingsService;

        public ServiceScopeFactoryStub(IApplicationSettingsService? settingsService = null)
        {
            _settingsService = settingsService;
        }

        public IServiceScope CreateScope() => new ServiceScopeStub(_settingsService);
    }

    private sealed class ServiceScopeStub : IServiceScope
    {
        public ServiceScopeStub(IApplicationSettingsService? settingsService)
        {
            var services = new ServiceCollection();
            if (settingsService != null)
            {
                services.AddSingleton(settingsService);
            }

            ServiceProvider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get; }

        public void Dispose() => (ServiceProvider as IDisposable)?.Dispose();
    }

    private sealed class RoutingActivationGate
    {
        public bool Activated { get; private set; }

        public void Activate() => Activated = true;
    }

    private sealed class GatedServiceModeResolver : IServiceModeResolver
    {
        private readonly RoutingActivationGate _gate;
        private readonly string _serviceName;
        private ServiceMode _localMode;

        public GatedServiceModeResolver(RoutingActivationGate gate, string serviceName, ServiceMode localMode)
        {
            _gate = gate;
            _serviceName = serviceName;
            _localMode = localMode;
        }

        public void SetModelId(string modelId) =>
            _localMode = _localMode with { ModelId = modelId };

        public Task<ServiceMode> ResolveAsync(string serviceName, string? modeId, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(serviceName, _serviceName, StringComparison.OrdinalIgnoreCase))
            {
                throw RoutingException.ModeNotFound(serviceName, modeId ?? "default");
            }

            if (!_gate.Activated)
            {
                throw RoutingException.ModeNotFound(serviceName, modeId ?? "default");
            }

            return Task.FromResult(_localMode);
        }

        public Task<IReadOnlyList<ServiceMode>> GetModesAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(serviceName, _serviceName, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<IReadOnlyList<ServiceMode>>(Array.Empty<ServiceMode>());
            }

            if (!_gate.Activated)
            {
                return Task.FromResult<IReadOnlyList<ServiceMode>>(Array.Empty<ServiceMode>());
            }

            return Task.FromResult<IReadOnlyList<ServiceMode>>(new[] { _localMode });
        }
    }

    private static IHttpClientFactory CreateHttpClientFactory() =>
        new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
}
