using System.Text.Json.Serialization;

namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Tier-1 skill projection materialized onto <see cref="AssistantDefinition"/>.
/// Carries metadata and file-path inventory only — never skill body bytes.
/// </summary>
public sealed class SkillDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("folder_path")]
    public string FolderPath { get; set; } = "";

    [JsonPropertyName("locator")]
    public string Locator { get; set; } = "";

    [JsonPropertyName("requires_tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RequiresTools { get; set; }

    [JsonPropertyName("requires_toolsets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RequiresToolsets { get; set; }

    [JsonPropertyName("fallback_for_tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FallbackForTools { get; set; }

    [JsonPropertyName("fallback_for_toolsets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FallbackForToolsets { get; set; }

    [JsonPropertyName("platforms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Platforms { get; set; }

    [JsonPropertyName("display_order")]
    public int DisplayOrder { get; set; }

    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = [];
}
