using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Configuration;
using Moq;
using System.Net;
using System.Text;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class ConfiguredLocalServiceSelectionSyncTests
{
    [TestMethod]
    public async Task ResolveOrSyncImageBundleAsync_NoLocalMode_ReturnsNullWithoutSet()
    {
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(RoutedServiceNames.ImageGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ServiceModeDto>());

        var result = await ConfiguredLocalServiceSelectionSync.ResolveOrSyncImageBundleAsync(
            settings.Object,
            "FLUX.2-dev",
            CancellationToken.None);

        result.Should().BeNull();
        settings.Verify(
            x => x.SetServiceModeModelIdAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task ResolveOrSyncImageBundleAsync_LocalModeExists_PersistsMarker()
    {
        const string bundleId = "FLUX.2-dev";
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(RoutedServiceNames.ImageGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    RoutedServiceNames.ImageGeneration,
                    "local",
                    "LocalServiceHosts:ImageGenerationBaseUrl",
                    null,
                    null,
                    Enabled: true,
                    IsDefault: true),
            ]);
        settings
            .Setup(x => x.SetServiceModeModelIdAsync(
                RoutedServiceNames.ImageGeneration,
                bundleId,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await ConfiguredLocalServiceSelectionSync.ResolveOrSyncImageBundleAsync(
            settings.Object,
            bundleId,
            CancellationToken.None);

        result.Should().Be(bundleId);
        settings.VerifyAll();
    }

    [TestMethod]
    public async Task ResolveOrSyncImageBundleAsync_ReturnsPersistedWithoutSet()
    {
        const string bundleId = "FLUX.2-dev";
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(RoutedServiceNames.ImageGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    RoutedServiceNames.ImageGeneration,
                    "local",
                    "LocalServiceHosts:ImageGenerationBaseUrl",
                    bundleId,
                    null,
                    Enabled: true,
                    IsDefault: true),
            ]);

        var result = await ConfiguredLocalServiceSelectionSync.ResolveOrSyncImageBundleAsync(
            settings.Object,
            "other-bundle",
            CancellationToken.None);

        result.Should().Be(bundleId);
        settings.Verify(
            x => x.SetServiceModeModelIdAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task SyncAllWarmLocalServicesAsync_PropagatesPersistFailure_WhenLocalRoutingWarm()
    {
        const string bundleId = "FLUX.2-dev";
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(RoutedServiceNames.ImageGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    RoutedServiceNames.ImageGeneration,
                    "local",
                    "LocalServiceHosts:ImageGenerationBaseUrl",
                    null,
                    null,
                    Enabled: true,
                    IsDefault: true),
            ]);
        settings
            .Setup(x => x.SetServiceModeModelIdAsync(
                RoutedServiceNames.ImageGeneration,
                bundleId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("persist failed"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://localhost:8112",
            })
            .Build();

        var httpClientFactory = new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"activeBundleMarkerId":"{{bundleId}}","items":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            });

        var act = () => ConfiguredLocalServiceSelectionSync.SyncAllWarmLocalServicesAsync(
            settings.Object,
            configuration,
            httpClientFactory,
            (serviceId, _) => Task.FromResult(
                string.Equals(serviceId, RoutedServiceNames.ImageGeneration, StringComparison.Ordinal)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("persist failed");
    }

    [TestMethod]
    public async Task SyncAllWarmLocalServicesAsync_PropagatesPersistFailure_ForAsrWhenLocalRoutingWarm()
    {
        const string modelRef = "/models/whisper-large";
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(RoutedServiceNames.SpeechTranscription, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    RoutedServiceNames.SpeechTranscription,
                    "local",
                    "LocalServiceHosts:SpeechTranscriptionBaseUrl",
                    null,
                    null,
                    Enabled: true,
                    IsDefault: true),
            ]);
        settings
            .Setup(x => x.SetServiceModeModelIdAsync(
                RoutedServiceNames.SpeechTranscription,
                modelRef,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("asr persist failed"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://localhost:8111",
            })
            .Build();

        var httpClientFactory = new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"items":[{"modelRef":"{{modelRef}}","active":true}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });

        var act = () => ConfiguredLocalServiceSelectionSync.SyncAllWarmLocalServicesAsync(
            settings.Object,
            configuration,
            httpClientFactory,
            (serviceId, _) => Task.FromResult(
                string.Equals(serviceId, RoutedServiceNames.SpeechTranscription, StringComparison.Ordinal)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("asr persist failed");
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHttpMessageHandler(responder));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
