using System.ComponentModel.DataAnnotations;

namespace GuideAntsApi.DataModel.Models;

public class ApplicationSetting
{
    public string SectionName { get; set; } = string.Empty;

    public string JsonValue { get; set; } = "{}";

    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
