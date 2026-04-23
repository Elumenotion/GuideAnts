using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Models;

[JsonConverter(typeof(NotebookAuthTypeConverter))]
public enum NotebookAuthType
{
    OAuth,
    ServiceHttp
}

public sealed class NotebookAuthTypeConverter : JsonConverter<NotebookAuthType>
{
    public override NotebookAuthType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s)) throw new JsonException("authType was null or empty");
        s = s.Trim();
        if (s.Equals("oauth", StringComparison.OrdinalIgnoreCase)) return NotebookAuthType.OAuth;
        if (s.Equals("service_http", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("service-http", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("serviceHttp", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("servicehttp", StringComparison.OrdinalIgnoreCase))
        {
            return NotebookAuthType.ServiceHttp;
        }
        throw new JsonException($"Invalid authType value: '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, NotebookAuthType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == NotebookAuthType.OAuth ? "oauth" : "service_http");
    }
}


