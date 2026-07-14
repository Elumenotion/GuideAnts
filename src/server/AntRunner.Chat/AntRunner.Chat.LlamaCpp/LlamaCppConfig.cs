namespace AntRunner.Chat.LlamaCpp;

public sealed class LlamaCppConfig
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 600;
    public List<string> OutputStripPatterns { get; set; } = [];
}
