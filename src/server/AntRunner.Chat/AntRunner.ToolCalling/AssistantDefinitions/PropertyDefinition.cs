namespace AntRunner.ToolCalling.AssistantDefinitions;

public class PropertyDefinition
{
    public enum FunctionObjectTypes
    {
        String,
        Integer,
        Number,
        Object,
        Array,
        Boolean,
        Null
    }

    /// <summary>
    /// Required. Function parameter object type. Default value is "object".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    /// <summary>
    /// Optional. Argument description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional. Example.
    /// </summary>
    [JsonPropertyName("example")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Example { get; set; }

    /// <summary>
    /// Optional. List of allowed values for this argument.
    /// </summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? Enum { get; set; }

    /// <summary>
    /// Optional. Argument description.
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Default { get; set; }

    /// <summary>
    /// Optional. The parameters the functions accepts, described as a JSON Schema object.
    /// See the <a href="https://platform.openai.com/docs/guides/gpt/function-calling">guide</a> for examples,
    /// and the <a href="https://json-schema.org/understanding-json-schema/">JSON Schema reference</a> for
    /// documentation about the format.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParametersDefinition? Parameters { get; set; }

    /// <summary>
    /// If type is "array", this specifies the element type for all items in the array.
    /// If type is not "array", this should be null.
    /// For more details, see https://json-schema.org/understanding-json-schema/reference/array.html
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParametersDefinition? Items { get; set; }

    public static PropertyDefinition DefineArray(ParametersDefinition? arrayItems = null)
    {
        return new()
        {
            Items = arrayItems,
            Type = ConvertTypeToString(FunctionObjectTypes.Array)
        };
    }

    public static PropertyDefinition DefineEnum(List<string> enumList, string? description = null)
    {
        return new()
        {
            Description = description,
            Enum = enumList,
            Type = ConvertTypeToString(FunctionObjectTypes.String)
        };
    }

    public static PropertyDefinition DefineInteger(string? description = null)
    {
        return new()
        {
            Description = description,
            Type = ConvertTypeToString(FunctionObjectTypes.Integer)
        };
    }

    public static PropertyDefinition DefineNumber(string? description = null)
    {
        return new()
        {
            Description = description,
            Type = ConvertTypeToString(FunctionObjectTypes.Number)
        };
    }

    public static PropertyDefinition DefineString(string? description = null)
    {
        return new()
        {
            Description = description,
            Type = ConvertTypeToString(FunctionObjectTypes.String)
        };
    }

    public static PropertyDefinition DefineBoolean(string? description = null)
    {
        return new()
        {
            Description = description,
            Type = ConvertTypeToString(FunctionObjectTypes.Boolean)
        };
    }

    public static PropertyDefinition DefineNull(string? description = null)
    {
        return new()
        {
            Description = description,
            Type = ConvertTypeToString(FunctionObjectTypes.Null)
        };
    }

    public static PropertyDefinition DefineObject(IDictionary<string, PropertyDefinition>? properties, string? description, string? defaultValue)
    {
        return new()
        {
            Description = description,
            Default = defaultValue,
            Type = ConvertTypeToString(FunctionObjectTypes.Object)
        };
    }

    /// <summary>
    /// Converts a FunctionObjectTypes enumeration value to its corresponding string representation.
    /// </summary>
    /// <param name="type">The type to convert</param>
    /// <returns>The string representation of the given type</returns>
    public static string ConvertTypeToString(FunctionObjectTypes type)
    {
        return type switch
        {
            FunctionObjectTypes.String => "string",
            FunctionObjectTypes.Integer => "integer",
            FunctionObjectTypes.Number => "number",
            FunctionObjectTypes.Object => "object",
            FunctionObjectTypes.Array => "array",
            FunctionObjectTypes.Boolean => "boolean",
            FunctionObjectTypes.Null => "null",
            _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown type: {type}")
        };
    }
}