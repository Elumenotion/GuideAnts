using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Reads API-owned local selections from ServiceModes.
/// Engine status, disk markers, and container inventory are never selection inputs.
/// </summary>
internal static class LocalServiceModeSelectionReader
{
    public static async Task<bool> HasLocalServiceModeAsync(
        IApplicationSettingsService settings,
        string serviceId,
        CancellationToken cancellationToken)
    {
        var localSection = ResolveLocalProviderSection(serviceId);
        if (localSection is null)
        {
            return false;
        }

        var modes = await settings.GetServiceModesAsync(serviceId, cancellationToken).ConfigureAwait(false);
        return modes.Any(mode =>
            string.Equals(mode.ProviderSection, localSection, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<string?> TryReadLocalModelRefAsync(
        IApplicationSettingsService settings,
        string serviceId,
        CancellationToken cancellationToken)
    {
        var localSection = ResolveLocalProviderSection(serviceId);
        if (localSection is null)
        {
            return null;
        }

        var modes = await settings.GetServiceModesAsync(serviceId, cancellationToken).ConfigureAwait(false);
        var localMode = modes.FirstOrDefault(mode =>
            string.Equals(mode.ProviderSection, localSection, StringComparison.OrdinalIgnoreCase));
        var selected = localMode?.ModelId?.Trim();
        return string.IsNullOrWhiteSpace(selected) ? null : selected;
    }

    private static string? ResolveLocalProviderSection(string serviceId) =>
        serviceId switch
        {
            RoutedServiceNames.SpeechTranscription =>
                $"{LocalServiceHostsOptions.SectionName}:SpeechTranscriptionBaseUrl",
            RoutedServiceNames.Embeddings =>
                $"{LocalServiceHostsOptions.SectionName}:EmbeddingsBaseUrl",
            RoutedServiceNames.SpeechSynthesis =>
                $"{LocalServiceHostsOptions.SectionName}:SpeechSynthesisBaseUrl",
            RoutedServiceNames.ImageGeneration =>
                $"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl",
            _ => null,
        };
}
