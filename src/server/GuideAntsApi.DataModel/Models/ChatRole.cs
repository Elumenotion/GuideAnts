using System.Text.Json.Serialization;

namespace GuideAntsApi.DataModel.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatRole
{
    System = 1,
    User = 2,
    Assistant = 3,
    Tool = 4
} 