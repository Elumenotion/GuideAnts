namespace AntRunner.Chat.Anthropic;

public static class AnthropicConfigFactory
{
    private static readonly AnthropicConfig Config;

    static AnthropicConfigFactory()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl)
            && baseUrl.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ANTHROPIC_BASE_URL must be the base endpoint without /v1/messages.");
        }

        var maxTokensRaw = Environment.GetEnvironmentVariable("ANTHROPIC_DEFAULT_MAX_TOKENS");
        if (!int.TryParse(maxTokensRaw, out var maxTokens) || maxTokens <= 0)
        {
            throw new InvalidOperationException("ANTHROPIC_DEFAULT_MAX_TOKENS must be a positive integer.");
        }

        Config = new AnthropicConfig
        {
            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            AuthToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"),
            BaseUrl = baseUrl,
            DefaultModel = Environment.GetEnvironmentVariable("ANTHROPIC_DEFAULT_MODEL"),
            DefaultMaxTokens = maxTokens,
            ThinkingBudgets = new AnthropicThinkingBudgets(
                Minimal: ParseBudget("ANTHROPIC_THINKING_BUDGET_MINIMAL"),
                Low: ParseBudget("ANTHROPIC_THINKING_BUDGET_LOW"),
                Medium: ParseBudget("ANTHROPIC_THINKING_BUDGET_MEDIUM"),
                High: ParseBudget("ANTHROPIC_THINKING_BUDGET_HIGH"))
        };
    }

    public static AnthropicConfig Get() => Config;

    private static int? ParseBudget(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{name} must be a positive integer.");
        }

        return value;
    }
}

public sealed record AnthropicConfig
{
    public string? ApiKey { get; init; }
    public string? AuthToken { get; init; }
    public string? BaseUrl { get; init; }
    public string? DefaultModel { get; init; }
    public int DefaultMaxTokens { get; init; }
    public AnthropicThinkingBudgets ThinkingBudgets { get; init; } = new();
}

public sealed record AnthropicThinkingBudgets(
    int? Minimal = null,
    int? Low = null,
    int? Medium = null,
    int? High = null)
{
    public int? ForEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "minimal" => Minimal,
            "low" => Low,
            "medium" => Medium,
            "high" => High,
            _ => null
        };
    }
}
