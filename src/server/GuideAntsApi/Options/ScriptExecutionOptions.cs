namespace GuideAntsApi.Options;

public sealed class ScriptExecutionOptions
{
    public const string SectionName = "ScriptExecution";

    /// <summary>
    /// Maximum time allowed for a single sandbox script execution request (API client and agent).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;

    public TimeSpan HttpClientTimeout => TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));
}
