namespace GuideAntsApi.Configuration;

public sealed class GuideAntsRuntimeOptions
{
    public const string SectionName = "GuideAntsRuntime";

    public string AffectedMountServices { get; set; } = string.Empty;
}
