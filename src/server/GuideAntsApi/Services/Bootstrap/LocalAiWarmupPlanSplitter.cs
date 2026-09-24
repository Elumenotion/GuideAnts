using System.Text.Json.Nodes;

namespace GuideAntsApi.Services.Bootstrap;

/// <summary>
/// Splits a complete API lifecycle plan into per-stack plans. The plan carries only
/// auxiliary (non-llama) services, each single-instance: services configured for that
/// stack keep API intent; all others are explicit <c>enabled: false</c> so loopback
/// engines on the wrong box unload. The plan never carries a llama section - llama
/// actuation is API-direct and must not be fanned out to any stack.
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
                // Llama is never in the plan (API-direct actuation): skip it so no
                // stack is ever told to load or unload a llama model.
                if (string.Equals(serviceId, LocalAiStackHostUrls.LlamaServiceId, StringComparison.Ordinal))
                {
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
}

public sealed record StackWarmupPlan(string StackBaseUrl, string PlanJson);
