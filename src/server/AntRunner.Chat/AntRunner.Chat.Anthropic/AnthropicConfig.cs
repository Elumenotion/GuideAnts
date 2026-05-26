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
    int? Minimal = 1024,
    int? Low = 2048,
    int? Medium = 4096,
    int? High = 8192)
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
