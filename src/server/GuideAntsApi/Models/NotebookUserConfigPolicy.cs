using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Models;

[JsonConverter(typeof(NotebookUserConfigPolicyConverter))]
public enum NotebookUserConfigPolicy
{
    None,
    Optional,
    Required
}

public sealed class NotebookUserConfigPolicyConverter : JsonConverter<NotebookUserConfigPolicy>
{
    public override NotebookUserConfigPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s)) return NotebookUserConfigPolicy.None;
        s = s.Trim();
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return NotebookUserConfigPolicy.None;
        if (s.Equals("optional", StringComparison.OrdinalIgnoreCase)) return NotebookUserConfigPolicy.Optional;
        if (s.Equals("required", StringComparison.OrdinalIgnoreCase)) return NotebookUserConfigPolicy.Required;
        throw new JsonException($"Invalid userConfigPolicy value: '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, NotebookUserConfigPolicy value, JsonSerializerOptions options)
    {
        var s = value switch
        {
            NotebookUserConfigPolicy.Optional => "optional",
            NotebookUserConfigPolicy.Required => "required",
            _ => "none"
        };
        writer.WriteStringValue(s);
    }
}


