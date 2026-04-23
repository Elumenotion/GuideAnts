namespace GuideAntsApi.Configuration;

/// <summary>
/// Options for delegated Hugging Face downloads. Model files and router aliases
/// live on the guideants-ai runtime; the API does not mount or read a host
/// model directory.
///
/// <para>
/// The Hugging Face token used for downloads does NOT live here. It is stored
/// once, in the top-level <c>HuggingFace</c> application-settings section
/// (property <c>Token</c>) and read through
/// <see cref="Services.HuggingFace.IHuggingFaceTokenResolver"/>. See
/// <see cref="Settings.SettingsSectionRegistry"/> for where the section is
/// defined and bootstrapped from the <c>HF_TOKEN</c> environment variable on
/// first install.
/// </para>
/// </summary>
public sealed class LlamaModelManagementOptions
{
    public const string SectionName = "LlamaModelManagement";

    /// <summary>
    /// When true, downloading an existing target file overwrites it. When
    /// false, the admin service returns an error if the file already exists.
    /// </summary>
    public bool AllowOverwrite { get; set; }
}
