namespace AntRunner.ToolCalling.AssistantDefinitions;

/// <summary>
/// Definition of a valid tool.
/// </summary>
public class ToolDefinition
{
    /// <summary>
    /// Required. The type of the tool. Currently, only function is supported.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }


    /// <summary>
    /// A list of functions the model may generate JSON inputs for.
    /// </summary>
    [JsonPropertyName("function")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssistantsApiToolFunctionOneOfType? Function { get; set; }

    [JsonIgnore]
    public object? FunctionsAsObject { get; set; }

    public static ToolDefinition DefineFunction(AssistantsApiToolFunctionOneOfType function)
    {
        return new()
        {
            Type = "function",
            Function = function
        };
    }

    public static ToolDefinition DefineCodeInterpreter()
    {
        return new()
        {
            Type = "code_interpreter"
        };
    }

    public static ToolDefinition DefineFileSearch()
    {
        return new()
        {
            Type = "file_search"
        };
    }
}

[JsonConverter(typeof(AssistantsApiToolFunctionConverter))]
public class AssistantsApiToolFunctionOneOfType
{
    [JsonIgnore]
    public string? AsString { get; set; }

    [JsonIgnore]
    public FunctionDefinition? AsObject { get; set; }
}

public class AssistantsApiToolFunctionConverter : JsonConverter<AssistantsApiToolFunctionOneOfType>
{
    public override AssistantsApiToolFunctionOneOfType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => new() { AsString = reader.GetString() },
            JsonTokenType.StartObject => new() { AsObject = JsonSerializer.Deserialize<FunctionDefinition>(ref reader, options) },
            _ => throw new JsonException()
        };
    }

    public override void Write(Utf8JsonWriter writer, AssistantsApiToolFunctionOneOfType? value, JsonSerializerOptions options)
    {
        if (value?.AsString != null)
        {
            writer.WriteStringValue(value.AsString);
        }
        else if (value?.AsObject != null)
        {
            JsonSerializer.Serialize(writer, value.AsObject, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}