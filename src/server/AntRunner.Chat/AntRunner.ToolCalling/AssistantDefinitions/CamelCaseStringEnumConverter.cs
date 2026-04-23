namespace AntRunner.ToolCalling.AssistantDefinitions
{
    /// <summary>
    /// Enables reading/writing enum values as lower camel-case strings ("low", "medium", "high").
    /// Also allows integer values for backward compatibility with database storage.
    /// </summary>
    internal sealed class CamelCaseStringEnumConverter : JsonStringEnumConverter
    {
        public CamelCaseStringEnumConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: true) { }
    }
} 