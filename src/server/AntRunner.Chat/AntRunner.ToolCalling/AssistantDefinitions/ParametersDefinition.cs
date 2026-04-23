namespace AntRunner.ToolCalling.AssistantDefinitions;

public class ParametersDefinition
{

    /// <summary>
    /// Required. Function parameter object type. Default value is "object".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    /// <summary>
    /// Optional. List of "function arguments", as a dictionary that maps from argument name
    /// to an object that describes the type, maybe possible enum values, and so on.
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, PropertyDefinition>? Properties { get; set; }

    /// <summary>
    /// Optional. List of "function arguments" which are required.
    /// </summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? Required { get; set; }
}