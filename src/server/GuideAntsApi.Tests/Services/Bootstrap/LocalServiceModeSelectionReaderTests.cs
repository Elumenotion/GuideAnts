using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalServiceModeSelectionReaderTests
{
    [TestMethod]
    public async Task TryReadLocalModelRefAsync_ReadsOnlyServiceModes()
    {
        const string bundleId = "flux2-klein-4b";
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(
                RoutedServiceNames.ImageGeneration,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ServiceModeDto(
                    RoutedServiceNames.ImageGeneration,
                    "cloud",
                    "OpenRouter",
                    "recraft/recraft-v4",
                    null,
                    Enabled: true,
                    IsDefault: true),
                new ServiceModeDto(
                    RoutedServiceNames.ImageGeneration,
                    "local",
                    "LocalServiceHosts:ImageGenerationBaseUrl",
                    bundleId,
                    null,
                    Enabled: true,
                    IsDefault: false),
            ]);

        var result = await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(
            settings.Object,
            RoutedServiceNames.ImageGeneration,
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
    public async Task TryReadLocalModelRefAsync_MissingSelectionReturnsNullWithoutMutation()
    {
        var settings = new Mock<IApplicationSettingsService>(MockBehavior.Strict);
        settings
            .Setup(x => x.GetServiceModesAsync(
                RoutedServiceNames.SpeechTranscription,
                It.IsAny<CancellationToken>()))
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

        var result = await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(
            settings.Object,
            RoutedServiceNames.SpeechTranscription,
            CancellationToken.None);

        result.Should().BeNull();
        settings.Verify(
            x => x.SetServiceModeModelIdAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
