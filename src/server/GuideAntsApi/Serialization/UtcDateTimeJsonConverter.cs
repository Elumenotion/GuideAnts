using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Serialization;

/// <summary>
/// Serializes <see cref="DateTime"/> values as UTC ISO-8601 strings with an explicit
/// "Z" suffix, and parses inbound strings into UTC <see cref="DateTime"/> values.
///
/// Why this exists: the store's convention is UTC at rest (DateTime.UtcNow writes,
/// GETUTCDATE() defaults, datetime2 columns), but datetime2 carries no offset, so EF
/// reads values back with Kind = Unspecified and System.Text.Json emits them without
/// the "Z" suffix. A Z-less string is an ambiguous timestamp; consumers (especially
/// JavaScript's new Date(...)) parse it as local wall-clock time and render the wrong
/// hour. This converter makes the wire format unambiguous in both directions.
///
/// Kind semantics:
/// - Utc: emitted as-is.
/// - Unspecified: treated as a UTC instant by store convention (marked Utc, no conversion).
/// - Local: converted to its UTC instant. A Local value reaching the API is a bug;
///   the SaveChanges guard (Phase 2) is what prevents it from being stored.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("Cannot read null as a non-nullable DateTime.");
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"Cannot convert empty string to DateTime: '{value}'.");
        }

        // AssumeUniversal: naive (zone-less) strings are UTC instants by store convention,
        // matching the client's parseUtcInstant contract. AdjustToUniversal: result Kind is
        // always Utc (AssumeUniversal alone yields Kind.Local on machines set to UTC).
        // "Z"/offset strings are converted to UTC with the instant preserved.
        return DateTime.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}
