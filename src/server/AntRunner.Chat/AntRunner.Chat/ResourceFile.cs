
namespace AntRunner.Chat
{
    /// <summary>
    /// Specifies the file's purpose
    /// </summary>
    public enum ResourceType
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        FileSearchToolResource,
        CodeInterpreterToolResource
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }

    /// <summary>
    /// Base64 file content for use by the assistant or thread
    /// </summary>
    public class ResourceFile
    {
        /// <summary>
        /// File path
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// File Id in azure open ai files
        /// </summary>
        public string FileId { get; set; } = string.Empty;

        /// <summary>
        /// Specifies the file's purpose
        /// </summary>
        public ResourceType ResourceType { get; set; }
    }
}
