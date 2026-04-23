namespace GuideAntsApi.Services.Conversations
{
    public interface IContextOptionsService
    {
        /// <summary>
        /// Resolve assistant context options for the specified user and project, performing placeholder substitution.
        /// User project-specific overrides are merged with assistant defaults, with user overrides taking precedence.
        /// </summary>
        Task<Dictionary<string, string>> ResolveAsync(AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition assistant, Guid projectId, Guid notebookId, Guid conversationId, CancellationToken ct = default);

        /// <summary>
        /// Builds the assistant-context system message (or null if no context values were found).
        /// </summary>
        Task<string?> BuildContextMessageAsync(AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition assistant, Guid projectId, Guid notebookId, Guid conversationId, CancellationToken ct = default);

        /// <summary>
        /// Builds context message for published conversations (no user context).
        /// Resolves placeholders like [@currentDate] but skips user-specific ones.
        /// Unresolved placeholders result in the entire key-value pair being omitted.
        /// </summary>
        string? BuildPublishedContextMessage(AntRunner.ToolCalling.AssistantDefinitions.AssistantDefinition assistant, Guid projectId, Guid notebookId);
    }
}
