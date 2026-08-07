using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;

namespace GuideAntsApi.Services.Routing;

/// <summary>
/// A resolved chat dispatch target. Carries the raw catalog row so the validator
/// and the routing factory can fan out without issuing another DB query.
/// <paramref name="ChatBehavior"/> is the row-owned chat behavior. llama-cpp requires it;
/// providers that accept row-owned request shaping (Hugging Face, OpenRouter) use it when
/// configured and fall back to their built-in mapping when it is empty.
/// </summary>
public sealed record ChatTarget(
    string ModelId,
    string Provider,
    string? RuntimeConfigJson,
    GuideAntsApi.Services.LlamaCpp.RuntimeProfileData? ChatBehavior = null);

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

        var provider = row.Provider.Trim();
        var isLlama = string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase);
        GuideAntsApi.Services.LlamaCpp.RuntimeProfileData? chatBehavior;
        try
        {
            chatBehavior = GuideAntsApi.Services.LlamaCpp.RuntimeProfileDataJson.FromJsonStrings(
                row.ModelId,
                row.CombineSystemAndDeveloperMessages,
                row.ThoughtBlockPattern,
                row.SamplingParametersJson,
                row.ThinkingControlJson,
                row.RequestFieldsWhenToolsPresentJson,
                row.DisplayName,
                row.Description);
        }
        catch (InvalidOperationException) when (!isLlama)
        {
            // Non-local rows only opt in to row-owned request shaping; a malformed surface must not
            // take chat down, so fall back to the provider client's built-in request mapping.
            chatBehavior = null;
        }

        return new ChatTarget(row.ModelId, provider, row.RuntimeConfigJson, chatBehavior);
    }
}
