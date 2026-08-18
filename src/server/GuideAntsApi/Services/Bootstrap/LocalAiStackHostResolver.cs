using GuideAntsApi.Configuration;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalAiStackHostResolver
{
    bool HasAnyConfiguredStack();

    IReadOnlyList<string> GetAllConfiguredStackBases();

    string? GetStackBaseForService(string serviceId);
}

public sealed class LocalAiStackHostResolver : ILocalAiStackHostResolver
{
    private readonly IConfiguration _configuration;

    public LocalAiStackHostResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool HasAnyConfiguredStack() => GetAllConfiguredStackBases().Count > 0;

    public IReadOnlyList<string> GetAllConfiguredStackBases()
    {
        var stacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serviceId in LocalAiStackHostUrls.WarmupServiceIds)
        {
            var stack = GetStackBaseForService(serviceId);
            if (stack is not null)
            {
                stacks.Add(stack);
            }
        }

        return stacks.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? GetStackBaseForService(string serviceId)
    {
        if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
        {
            return LocalAiStackHostUrls.NormalizeStackBaseUrl(_configuration["LlamaCpp:BaseUrl"]);
        }

        var configKey = ResolveLocalServiceHostConfigKey(serviceId);
        if (configKey is null)
        {
            return null;
        }

        return LocalAiStackHostUrls.NormalizeStackBaseUrl(_configuration[configKey]);
    }

    private static string? ResolveLocalServiceHostConfigKey(string serviceId) =>
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
