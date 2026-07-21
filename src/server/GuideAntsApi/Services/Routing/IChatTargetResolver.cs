using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// A resolved chat dispatch target. Carries the raw catalog row so the validator
/// and the routing factory can fan out without issuing another DB query.
/// </summary>
public sealed record ChatTarget(
    string ModelId,
    string Provider,
    string? RuntimeConfigJson,
    GuideAntsApi.Services.LlamaCpp.RuntimeProfileData? LlamaChatBehavior = null);

public interface IChatTargetResolver
{
    /// <summary>
    /// Resolves the catalog row for a chat deployment id. Throws
    /// <see cref="RoutingException"/> when the id is blank (R-9.1: no silent
    /// fallback) or the model is missing from the catalog.
    /// </summary>
    ChatTarget Resolve(string? modelId);
}

public sealed class ChatTargetResolver : IChatTargetResolver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ChatTargetResolver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public ChatTarget Resolve(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new RoutingException(
                RoutingErrorCodes.ModelNotReady,
                "Chat model id is required. No provider fallback is configured.",
                action: "Assign a chat model to the assistant in Settings → Models & Runtime and retry.",
                serviceId: "Chat");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = db.Models
            .AsNoTracking()
            .Where(m => m.ModelId == modelId)
            .Select(m => new
            {
                m.ModelId,
                m.Provider,
                m.RuntimeConfigJson,
                m.DisplayName,
                m.Description,
                m.CombineSystemAndDeveloperMessages,
                m.ThoughtBlockPattern,
                m.SamplingParametersJson,
                m.ThinkingControlJson,
                m.RequestFieldsWhenToolsPresentJson
            })
            .FirstOrDefault();

        if (row == null)
        {
            throw RoutingException.ModelNotReady(
                modelId,
                "Model not found in the catalog.",
                serviceId: "Chat",
                action: $"Open Settings → Models & Runtime and add a catalog row for '{modelId}', or point the assistant at an existing model.");
        }

        if (string.IsNullOrWhiteSpace(row.Provider))
        {
            throw RoutingException.ProviderNotReady(
                providerSection: "(unknown)",
                blockers: new[] { $"Model '{modelId}' is missing provider configuration." },
                serviceId: "Chat");
        }

        GuideAntsApi.Services.LlamaCpp.RuntimeProfileData? llamaBehavior = null;
        if (string.Equals(row.Provider.Trim(), "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            llamaBehavior = GuideAntsApi.Services.LlamaCpp.RuntimeProfileDataJson.FromJsonStrings(
                row.ModelId,
                row.CombineSystemAndDeveloperMessages,
                row.ThoughtBlockPattern,
                row.SamplingParametersJson,
                row.ThinkingControlJson,
                row.RequestFieldsWhenToolsPresentJson,
                row.DisplayName,
                row.Description);
        }

        return new ChatTarget(row.ModelId, row.Provider.Trim(), row.RuntimeConfigJson, llamaBehavior);
    }
}
