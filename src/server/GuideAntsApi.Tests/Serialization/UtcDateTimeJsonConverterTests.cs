using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Serialization;

namespace GuideAntsApi.Tests.Serialization;

[TestClass]
public sealed class UtcDateTimeJsonConverterTests
{
    private static readonly DateTime Instant = new(2026, 10, 23, 10, 30, 15, 123, DateTimeKind.Utc);

    [TestMethod]
    public void WebOptions_Serialize_UsesUtcZSuffix_ForAllKinds()
    {
        var expected = "\"2026-10-23T10:30:15.123Z\"";
        JsonSerializer.Serialize(Instant, JsonDefaults.CreateWebApiOptions()).Should().Be(expected);
        JsonSerializer.Serialize(DateTime.SpecifyKind(Instant, DateTimeKind.Unspecified), JsonDefaults.CreateWebApiOptions()).Should().Be(expected);
    }

    [TestMethod]
    public void WebOptions_Serialize_LocalKind_ConvertsToUtcWithZSuffix()
    {
        // A Local-kind value is converted to its UTC instant (machine-offset dependent), never emitted as local wall-clock.
        var local = DateTime.Now;
        var json = JsonSerializer.Serialize(local, JsonDefaults.CreateWebApiOptions());
        var expected = local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        json.Should().Be($"\"{expected}\"");
    }

    [TestMethod]
    public void WebOptions_Serialize_Nullable_Null_UsesNull()
    {
        DateTime? value = null;
        JsonSerializer.Serialize(value, JsonDefaults.CreateWebApiOptions()).Should().Be("null");
    }

    [TestMethod]
    public void WebOptions_Serialize_Nullable_Value_UsesUtcZSuffix()
    {
        DateTime? value = Instant;
        JsonSerializer.Serialize(value, JsonDefaults.CreateWebApiOptions()).Should().Be("\"2026-10-23T10:30:15.123Z\"");
    }

    [TestMethod]
    public void WebOptions_Deserialize_ZSuffix_ProducesUtcKind()
    {
        var parsed = JsonSerializer.Deserialize<DateTime>("\"2026-10-23T10:30:15.123Z\"", JsonDefaults.CreateWebApiOptions());
        parsed.Kind.Should().Be(DateTimeKind.Utc);
        parsed.Should().Be(Instant);
    }

    [TestMethod]
    public void WebOptions_Deserialize_NaiveString_IsTreatedAsUtc()
    {
        // Legacy Z-less values (pre-converter rows/exports) round-trip as UTC, matching the store convention.
        var parsed = JsonSerializer.Deserialize<DateTime>("\"2026-10-23T10:30:15.123\"", JsonDefaults.CreateWebApiOptions());
        parsed.Kind.Should().Be(DateTimeKind.Utc);
        parsed.Should().Be(Instant);
    }

    [TestMethod]
    public void WebOptions_RoundTrip_PreservesInstant()
    {
        var json = JsonSerializer.Serialize(Instant, JsonDefaults.CreateWebApiOptions());
        var back = JsonSerializer.Deserialize<DateTime>(json, JsonDefaults.CreateWebApiOptions());
        back.ToUniversalTime().Should().Be(Instant);
    }

    [TestMethod]
    public void SseOptions_Serialize_UsesUtcZSuffix()
    {
        JsonSerializer.Serialize(Instant, JsonDefaults.CreateSseOptions()).Should().Be("\"2026-10-23T10:30:15.123Z\"");
    }

    [TestMethod]
    public void SseOptions_CamelCase_StillApplied()
    {
        var json = JsonSerializer.Serialize(new { CreatedUtc = Instant }, JsonDefaults.CreateSseOptions());
        json.Should().Be("{\"createdUtc\":\"2026-10-23T10:30:15.123Z\"}");
    }

    [TestMethod]
    public void SseOptions_Enums_DefaultNumbers_Unchanged()
    {
        // The SSE payload contract uses default enum serialization (numbers), not strings.
        var json = JsonSerializer.Serialize(new { Mode = SomeFlag.Yes }, JsonDefaults.CreateSseOptions());
        json.Should().Be("{\"mode\":1}");
    }

    [TestMethod]
    public void WebOptions_Enums_SerializeAsStrings_StillApplied()
    {
        // Pre-existing contract: JsonStringEnumConverter emits PascalCase names
        // (behavior identical to the old inline converter registration).
        var json = JsonSerializer.Serialize(new { Mode = SomeFlag.Yes }, JsonDefaults.CreateWebApiOptions());
        json.Should().Be("{\"mode\":\"Yes\"}");
    }

    private enum SomeFlag
    {
        No,
        Yes
    }
}
