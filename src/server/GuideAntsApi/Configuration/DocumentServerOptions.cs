namespace GuideAntsApi.Configuration;

public sealed class DocumentServerOptions
{
    public const string SectionName = "DocumentServer";

    public bool Enabled { get; set; } = false;

    public string InternalUrl { get; set; } = "http://documentserver";

    public string ApiBaseUrl { get; set; } = string.Empty;

    public bool JwtEnabled { get; set; } = false;

    public string JwtSecret { get; set; } = string.Empty;

    public string JwtHeader { get; set; } = "Authorization";

    public bool JwtInBody { get; set; } = false;
}
