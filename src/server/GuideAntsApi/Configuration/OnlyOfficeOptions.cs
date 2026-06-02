namespace GuideAntsApi.Configuration;

public sealed class OnlyOfficeOptions
{
    public const string SectionName = "OnlyOffice";

    public bool Enabled { get; set; } = false;

    public string PublicUrl { get; set; } = string.Empty;

    public string InternalUrl { get; set; } = "http://onlyoffice-documentserver";

    public string ApiBaseUrl { get; set; } = string.Empty;

    public bool JwtEnabled { get; set; } = false;

    public string JwtSecret { get; set; } = string.Empty;

    public string JwtHeader { get; set; } = "Authorization";

    public bool JwtInBody { get; set; } = false;
}
