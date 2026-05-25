using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.AssistantDefinitions;

namespace AntRunner.Chat
{
    public delegate void MessageAddedEventHandler(object? sender, MessageAddedEventArgs e);

    public class MessageAddedEventArgs : EventArgs
    {
        public string Message { get; }
        public string Role { get; }
        public string? ToolCallId { get; }
        public string? FunctionName { get; }
        public string? ToolCallsJson { get; }

        public MessageAddedEventArgs(string role, string newMessage, string? toolCallId = null, string? functionName = null, string? toolCallsJson = null)
        {
            Message = newMessage;
            Role = role;
            ToolCallId = toolCallId;
            FunctionName = functionName;
            ToolCallsJson = toolCallsJson;
        }
    }

    public delegate void StreamingMessageProgressEventHandler(object? sender, StreamingMessageProgressEventArgs e);

    public delegate void ExternalToolCallEventHandler(object? sender, ExternalToolCallEventArgs e);

    public class ExternalToolCallEventArgs : EventArgs
    {
        public string ToolCallsJson { get; }
        public ExternalToolCallEventArgs(string toolCallsJson)
        {
            ToolCallsJson = toolCallsJson;
        }
    }

    public class StreamingMessageProgressEventArgs : EventArgs
    {
        public string ContentDelta { get; }
        public string Role { get; }

        public StreamingMessageProgressEventArgs(string role, string contentDelta)
        {
            ContentDelta = contentDelta;
            Role = role;
        }
    }

    /// <summary>
    /// Represents a conversation with an AI assistant, managing the interaction and message history.
    /// </summary>
    public class Conversation
    {
        private ChatRunOptions? _chatConfiguration;
        private HttpClient _httpClient = HttpClientUtility.Get();

        /// <summary>
        /// Gets or sets the messages exchanged with the assistant.
        /// </summary>
        public Dictionary<string, List<ChatMessage>> AssistantMessages { get; set; } = [];

        /// <summary>
        /// Gets or sets the list of turns in the conversation.
        /// </summary>
        public List<Turn> Turns { get; set; } = [];
 
        /// <summary>
        /// Initializes a new instance of the <see cref="Conversation"/> class.
        /// </summary>
        public Conversation()
        {
            // Parameterless constructor for deserialization
        }

        private Conversation(ChatRunOptions chatConfiguration, AssistantDefinition assistantDef)
        {
            _chatConfiguration = chatConfiguration;
            AssistantDefinition = assistantDef;
            AssistantMessages[AssistantDefinition.Name!] = [];
        }

        /// <summary>
        /// Gets the definition of the assistant being used in the conversation.
        /// </summary>
        public AssistantDefinition? AssistantDefinition { get; set; }

        /// <summary>
        /// Changes the assistant being used in the conversation to the specified assistant name.
        /// </summary>
        /// <param name="assistantName">The name of the new assistant to use.</param>
        /// <param name="executionPolicy">Resolved execution policy for the target assistant.</param>
        public async Task ChangeAssistant(string assistantName, ResolvedExecutionPolicy executionPolicy)
        {
            if (AssistantDefinition == null) throw new Exception("AssistantDefinition is null. Use the default public constructor exists to allow serialization, but you should nt use it directly.");
            if (_chatConfiguration == null) throw new Exception("_chatConfiguration is null. Use the default public constructor exists to allow serialization, but you should nt use it directly.");
            ArgumentNullException.ThrowIfNull(executionPolicy);

            if (assistantName == AssistantDefinition.Name) return;
            var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName) ?? throw new Exception($"Can't find assistant definition for {assistantName}");

            var existingMessages = AssistantMessages[AssistantDefinition.Name!]
                .Where(m => m.Role == ChatRole.User || m.Role == ChatRole.Assistant && m.ToolCalls == null)
                .ToList();

            // Create new message list with system prompts first, then history, then handoff message
            var newAssistantMessages = new List<ChatMessage>();
            
            // Add system prompts at the beginning
            newAssistantMessages.Add(new ChatMessage(ChatRole.System, assistantDef.Instructions ?? string.Empty));
            
            // Add filtered existing messages
            newAssistantMessages.AddRange(existingMessages);
            
            // Add handoff message AFTER the history (so "above" makes sense)
            newAssistantMessages.Add(new ChatMessage(ChatRole.System,
                "The previous messages between the user and assistant above are from a conversation with a different assistant. " +
                "Use them to understand the conversation context, but follow the system messages that were provided at the start of this message sequence."));

            AssistantDefinition = assistantDef;
            AssistantMessages[assistantName] = newAssistantMessages;

            _chatConfiguration.AssistantName = assistantName;
            _chatConfiguration.DeploymentId = executionPolicy.ModelId;
            _chatConfiguration.ExecutionPolicy = executionPolicy;
        }

        /// <summary>
        /// Creates a new conversation asynchronously using the specified chat configuration.
        /// </summary>
        /// <param name="chatConfiguration">The configuration options for the chat.</param>
        /// <returns>A task representing the newly created conversation.</returns>
        public static async Task<Conversation> Create(ChatRunOptions chatConfiguration, HttpClient? httpClient = null)
        {
            var assistantDef = await AssistantUtility.GetAssistantCreateRequest(chatConfiguration.AssistantName) ?? throw new Exception($"Can't find assistant definition for {chatConfiguration.AssistantName}");
            var conversation = new Conversation(chatConfiguration, assistantDef);
            conversation._httpClient = httpClient ?? conversation._httpClient;
            return conversation;
        }
    }
}
