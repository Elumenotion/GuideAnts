namespace AntRunner.ToolCalling.AssistantDefinitions;

public class ToolResources
{
    [JsonPropertyName("code_interpreter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodeInterpreter? CodeInterpreter { get; set; }

    [JsonPropertyName("file_search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileSearch? FileSearch { get; set; }
}

public class CodeInterpreter
{
    /// <summary>
    /// A list of file IDs made available to the code_interpreter tool. There can be a maximum of 20 files associated with
    /// the tool.
    /// </summary>
    [JsonPropertyName("file_ids")]
    public List<string>? FileIds { get; set; }
}

public class FileSearch
{
    /// <summary>
    /// The vector store attached to this assistant. There can be a maximum of 1 vector store attached to the assistant.
    /// </summary>
    [JsonPropertyName("vector_store_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? VectorStoreIds { get; set; }

    /// <summary>
    /// A helper to create a vector store with file_ids and attach it to this assistant. There can be a maximum of 1 vector
    /// store attached to the assistant.
    /// </summary>
    [JsonPropertyName("vector_stores")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VectorStores>? VectorStores { get; set; }
}

public class VectorStores
{
    /// <summary>
    /// A list of file IDs to add to the vector store. There can be a maximum of 10000 files in a vector store.
    /// </summary>
    [JsonPropertyName("file_ids")]
    public List<string>? FileIds { get; set; }

    /// <summary>
    /// Set of 16 key-value pairs that can be attached to a vector store. This can be useful for storing additional
    /// information about the vector store in a structured format. Keys can be a maximum of 64 characters long and values
    /// can be a maxium of 512 characters long.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

[JsonConverter(typeof(ResponseFormatOptionConverter))]
public class ResponseFormatOneOfType
{
    public ResponseFormatOneOfType()
    {
    }

    public ResponseFormatOneOfType(string asString)
    {
        AsString = asString;
    }

    public ResponseFormatOneOfType(ResponseFormat asObject)
    {
        AsObject = asObject;
    }

    [JsonIgnore]
    public string? AsString { get; set; }

    [JsonIgnore]
    public ResponseFormat? AsObject { get; set; }
}

public class ResponseFormatOptionConverter : JsonConverter<ResponseFormatOneOfType>
{
    public override ResponseFormatOneOfType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => new() { AsString = reader.GetString() },
            JsonTokenType.StartObject => new() { AsObject = JsonSerializer.Deserialize<ResponseFormat>(ref reader, options) },
            _ => throw new JsonException()
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseFormatOneOfType? value, JsonSerializerOptions options)
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

