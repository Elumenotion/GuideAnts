using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Splits a complete API lifecycle plan into per-stack plans. Llama sections are
/// per-instance (named <c>llama.&lt;instance base&gt;</c>): each stack receives its own
/// section verbatim and every other instance's section is dropped, so an apply never
/// says "unload llama" to an instance whose section is not addressed to it. Auxiliary
/// (non-llama) services stay single-instance: services configured for that stack keep
/// API intent; all others are explicit <c>enabled: false</c> so loopback engines on the
/// wrong box unload.
/// </summary>
public sealed class LocalAiWarmupPlanSplitter
{
    private readonly ILocalAiStackHostResolver _stackHostResolver;

    public LocalAiWarmupPlanSplitter(ILocalAiStackHostResolver stackHostResolver)
    {
        _stackHostResolver = stackHostResolver;
    }

    public IReadOnlyList<StackWarmupPlan> Split(string planJson)
    {
        var root = JsonNode.Parse(planJson) as JsonObject
            ?? throw new InvalidOperationException("Lifecycle plan must be a JSON object.");
        var schemaVersion = root["schemaVersion"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Lifecycle plan must include schemaVersion.");
        var services = root["services"] as JsonObject
            ?? throw new InvalidOperationException("Lifecycle plan must include a services object.");

        var stacks = _stackHostResolver.GetAllConfiguredStackBases();
        if (stacks.Count == 0)
        {
            return Array.Empty<StackWarmupPlan>();
        }

        var results = new List<StackWarmupPlan>(stacks.Count);
        foreach (var stackBase in stacks)
        {
            var stackServices = new JsonObject();
            foreach (var serviceId in LocalAiStackHostUrls.WarmupServiceIds)
            {
                if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
                {
                    // Per-instance llama: pass this stack's own section through untouched,
                    // matched by CANONICAL identity so one physical box configured under
                    // several host names receives one section, not one per name.
                    var globalLlamaBase = _stackHostResolver.GetStackBaseForService(LocalAiStackHostUrls.LlamaServiceId);
                    var section = ResolveLlamaSectionForStack(
                        services,
                        LocalAiStackHostResolver.CanonicalInstanceKey(stackBase),
                        globalLlamaBase is null ? null : LocalAiStackHostResolver.CanonicalInstanceKey(globalLlamaBase));
                    stackServices[serviceId] = section;
                    continue;
                }

                var serviceStack = _stackHostResolver.GetStackBaseForService(serviceId);
                if (serviceStack is not null
                    && string.Equals(serviceStack, stackBase, StringComparison.OrdinalIgnoreCase)
                    && services.TryGetPropertyValue(serviceId, out var sectionNode)
                    && sectionNode is JsonObject sectionObject)
                {
                    stackServices[serviceId] = sectionObject.DeepClone();
                }
                else
                {
                    stackServices[serviceId] = new JsonObject { ["enabled"] = false };
                }
            }

            var stackPlan = new JsonObject
            {
                ["schemaVersion"] = schemaVersion,
                ["services"] = stackServices,
            };

            results.Add(new StackWarmupPlan(
                stackBase,
                stackPlan.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false })));
        }

        return results;
    }

    /// <summary>
    /// The llama section addressed to stackBase"/>:
    /// <c>llama.&lt;stackBase&gt;</c> when the plan names it, falling back to a plain
    /// <c>llama</c> section for global-plan compatibility. Missing = disabled (the
    /// only unload source; the builder always emits a section for every known instance).
    /// </summary>
    internal static JsonObject ResolveLlamaSectionForStack(JsonObject services, string canonicalInstanceKey, string? globalLlamaCanonicalKey)
    {
        var instanceKey = $"{LocalAiStackHostUrls.LlamaServiceId}.{canonicalInstanceKey}";
        JsonNode? instanceNode = services[instanceKey];
        if (instanceNode is JsonObject instanceSection)
        {
            return (JsonObject)instanceSection.DeepClone();
        }

        // Plain "llama" is a legacy/global-plan compatibility fallback, valid only for the
        // configured global llama stack itself.
        if (globalLlamaCanonicalKey is not null
            && string.Equals(globalLlamaCanonicalKey, canonicalInstanceKey, StringComparison.OrdinalIgnoreCase))
        {
            JsonNode? globalNode = services[LocalAiStackHostUrls.LlamaServiceId];
            if (globalNode is JsonObject globalSection)
            {
                return (JsonObject)globalSection.DeepClone();
            }
        }

        return new JsonObject { ["enabled"] = false };
    }
}

public sealed record StackWarmupPlan(string StackBaseUrl, string PlanJson);
