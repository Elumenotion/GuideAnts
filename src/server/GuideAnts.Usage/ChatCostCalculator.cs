namespace GuideAnts.Usage;

/// <summary>
/// Calculates costs for chat completion usage events based on token counts and model pricing.
/// </summary>
public static class ChatCostCalculator
{
    /// <summary>
    /// Calculates the cost in USD for a chat completion based on token usage and model.
    /// </summary>
    /// <param name="metrics">Token usage metrics</param>
    /// <param name="modelDeploymentId">Model deployment identifier for pricing lookup</param>
    /// <returns>Cost in USD</returns>
    public static decimal CalculateCost(UsageMetrics metrics, string? modelDeploymentId)
    {
        if (string.IsNullOrWhiteSpace(modelDeploymentId))
        {
            return 0m;
        }

        var pricing = GetModelPricing(modelDeploymentId);

        var inputCost = (metrics.ValueInput * pricing.InputPricePerToken) +
                       (metrics.ValueCachedInput * pricing.CachedInputPricePerToken);
        var reasoningCost = metrics.ValueReasoning * pricing.ReasoningPricePerToken;
        var outputCost = metrics.ValueOutput * pricing.OutputPricePerToken;

        return inputCost + reasoningCost + outputCost;
    }

    private static ModelPricing GetModelPricing(string modelDeploymentId)
    {
        return modelDeploymentId.ToLowerInvariant() switch
        {
            "gpt-5.1" => new ModelPricing
            {
                InputPricePerToken = 0.00000125m,      // $1.25 per 1M tokens
                CachedInputPricePerToken = 0.000000125m, // $0.125 per 1M tokens
                ReasoningPricePerToken = 0.00001m,     // $10.00 per 1M tokens
                OutputPricePerToken = 0.00001m         // $10.00 per 1M tokens
            },
            "gpt-5" => new ModelPricing
            {
                InputPricePerToken = 0.00000125m,      // $1.25 per 1M tokens
                CachedInputPricePerToken = 0.000000125m, // $0.125 per 1M tokens
                ReasoningPricePerToken = 0.00001m,     // $10.00 per 1M tokens
                OutputPricePerToken = 0.00001m         // $10.00 per 1M tokens
            },
            "gpt-5-mini" => new ModelPricing
            {
                InputPricePerToken = 0.00000025m,      // $0.25 per 1M tokens
                CachedInputPricePerToken = 0.000000025m, // $0.025 per 1M tokens
                ReasoningPricePerToken = 0.000002m,    // $2.00 per 1M tokens
                OutputPricePerToken = 0.000002m        // $2.00 per 1M tokens
            },
            "gpt-5-nano" => new ModelPricing
            {
                InputPricePerToken = 0.00000005m,      // $0.05 per 1M tokens
                CachedInputPricePerToken = 0.000000005m, // $0.005 per 1M tokens
                ReasoningPricePerToken = 0.0000004m,   // $0.40 per 1M tokens
                OutputPricePerToken = 0.0000004m       // $0.40 per 1M tokens
            },
            "gpt-5-chat" => new ModelPricing
            {
                InputPricePerToken = 0.00000125m,      // $1.25 per 1M tokens
                CachedInputPricePerToken = 0.000000125m, // $0.125 per 1M tokens
                ReasoningPricePerToken = 0.00001m,     // $10.00 per 1M tokens
                OutputPricePerToken = 0.00001m         // $10.00 per 1M tokens
            },
            "gpt-5-pro" => new ModelPricing
            {
                InputPricePerToken = 0.000015m,      // $15 per 1M tokens
                CachedInputPricePerToken = 0.000015m,      // $15 per 1M tokens
                ReasoningPricePerToken = 0.000015m,      // $15 per 1M tokens
                OutputPricePerToken = 0.00012m         // $120 per 1M tokens
            },
            "gpt-5.2-codex" => new ModelPricing
            {
                InputPricePerToken = 0.00000175m,      // $1.75 per 1M tokens
                CachedInputPricePerToken = 0.000000175m, // $0.175 per 1M tokens
                ReasoningPricePerToken = 0.000014m,    // $14.00 per 1M tokens
                OutputPricePerToken = 0.000014m        // $14.00 per 1M tokens
            },
            "gpt-4.1" => new ModelPricing
            {
                InputPricePerToken = 0.000002m,        // $2 per 1M tokens
                CachedInputPricePerToken = 0.0000005m, // $0.50 per 1M tokens
                ReasoningPricePerToken = 0.000008m,    // $8 per 1M tokens
                OutputPricePerToken = 0.000008m        // $8 per 1M tokens
            },
            "gpt-4.1-mini" => new ModelPricing
            {
                InputPricePerToken = 0.0000004m,       // $0.40 per 1M tokens
                CachedInputPricePerToken = 0.0000001m, // $0.10 per 1M tokens
                ReasoningPricePerToken = 0.0000016m,   // $1.60 per 1M tokens
                OutputPricePerToken = 0.0000016m       // $1.60 per 1M tokens
            },
            "gpt-4.1-nano" => new ModelPricing
            {
                InputPricePerToken = 0.0000001m,       // $0.10 per 1M tokens
                CachedInputPricePerToken = 0.00000003m, // $0.03 per 1M tokens
                ReasoningPricePerToken = 0.0000004m,   // $0.40 per 1M tokens
                OutputPricePerToken = 0.0000004m       // $0.40 per 1M tokens
            },
            var model when model.StartsWith("claude-opus-4-5") => new ModelPricing
            {
                InputPricePerToken = 0.000005m,        // $5.00 per 1M tokens
                CachedInputPricePerToken = 0.0000005m, // $0.50 per 1M tokens
                ReasoningPricePerToken = 0.000025m,    // $25.00 per 1M tokens
                OutputPricePerToken = 0.000025m        // $25.00 per 1M tokens
            },
            var model when model.StartsWith("claude-sonnet-4-5") => new ModelPricing
            {
                InputPricePerToken = 0.000003m,        // $3.00 per 1M tokens
                CachedInputPricePerToken = 0.0000003m, // $0.30 per 1M tokens
                ReasoningPricePerToken = 0.000015m,    // $15.00 per 1M tokens
                OutputPricePerToken = 0.000015m        // $15.00 per 1M tokens
            },
            var model when model.StartsWith("claude-haiku-4-5") => new ModelPricing
            {
                InputPricePerToken = 0.000001m,        // $1.00 per 1M tokens
                CachedInputPricePerToken = 0.0000001m, // $0.10 per 1M tokens
                ReasoningPricePerToken = 0.000005m,    // $5.00 per 1M tokens
                OutputPricePerToken = 0.000005m        // $5.00 per 1M tokens
            },
            _ => new ModelPricing // Default to GPT-4.1 pricing for unknown models
            {
                InputPricePerToken = 0.000002m,        // $2 per 1M tokens
                CachedInputPricePerToken = 0.0000005m, // $0.50 per 1M tokens
                ReasoningPricePerToken = 0.000008m,    // $8 per 1M tokens
                OutputPricePerToken = 0.000008m        // $8 per 1M tokens
            }
        };
    }

    private record ModelPricing
    {
        public decimal InputPricePerToken { get; init; }
        public decimal CachedInputPricePerToken { get; init; }
        public decimal ReasoningPricePerToken { get; init; }
        public decimal OutputPricePerToken { get; init; }
    }
}
