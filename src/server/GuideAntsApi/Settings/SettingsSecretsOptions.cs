namespace GuideAntsApi.Settings;

public sealed class SettingsSecretsOptions
{
    public const string SectionName = "SettingsSecrets";

    public string ActiveKeyId { get; set; } = string.Empty;

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}