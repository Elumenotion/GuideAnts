namespace GuideAntsApi.Options;

public sealed class SandboxWireApiOptions
{
    public const string SectionName = "SandboxWireApi";

    public const string DefaultAudience = "GuideAnts.SandboxWire";

    public string Issuer { get; set; } = "GuideAnts";

    public string Audience { get; set; } = DefaultAudience;

    public string SigningKey { get; set; } = string.Empty;

    public int DefaultLifetimeMinutes { get; set; } = 35;

    public string InternalBaseUrl { get; set; } = "http://guideants-webapi-ui:8080/api/internal/sandbox/openai/v1";
}
