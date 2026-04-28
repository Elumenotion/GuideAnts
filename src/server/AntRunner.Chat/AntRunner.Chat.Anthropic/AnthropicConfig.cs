namespace AntRunner.Chat.Anthropic;

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
