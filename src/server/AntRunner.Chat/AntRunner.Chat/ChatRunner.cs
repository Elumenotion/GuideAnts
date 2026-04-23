using AntRunner.ToolCalling;
using AntRunner.Chat.Abstractions;

namespace AntRunner.Chat
{
    public class ChatConversationException : Exception
    {
        public ChatRunOutput? ChatRunOutput { get; private set; }

        public ChatConversationException(Exception ex, ChatRunOutput? chatRunOutput) : base("An error occured during the run", ex)
        {
            ChatRunOutput = chatRunOutput;
        }
    }

    /// <summary>
    /// Responsible for running assistant threads through interaction with various utilities.
    /// </summary>
    public class ChatRunner
    {

        /// <summary>
        /// Executes a chat conversation thread with an AI assistant.
        /// </summary>
        /// <param name="chatRunOptions">Options and settings for running the chat, including assistant name and instructions.</param>
        /// <param name="clientFactory">Client factory for the configured chat provider.</param>
        /// <param name="previousMessages">Optional list of messages from a previous conversation for context continuity.</param>
        /// <param name="httpClient">Optional HttpClient instance for making HTTP requests, with a default fallback if not provided.</param>
        /// <param name="messageAdded">Optional event handler for when a message is added.</param>
        /// <param name="streamingMessageProgress">Optional event handler for streaming message progress.</param>
        /// <param name="projectId">Optional project ID for notebook context injection.</param>
        /// <param name="notebookId">Optional notebook ID for notebook context injection.</param>
        /// <returns>
        /// Returns a <see cref="ChatRunOutput"/> containing the results of the chat run, including the final state of the conversation.
        /// </returns>
        /// <exception cref="Exception">Thrown when the assistant definition cannot be found.</exception>
        /// <exception cref="ChatConversationException">Thrown when an error occurs during the chat conversation, encapsulating the current run results.</exception>
        public static async Task<ChatRunOutput?> RunThread(
            ChatRunOptions chatRunOptions,
            IChatCompletionClientFactory clientFactory,
            List<ChatMessage>? previousMessages = null,
            HttpClient? httpClient = null,
            MessageAddedEventHandler? messageAdded = null,
            StreamingMessageProgressEventHandler? streamingMessageProgress = null,
            string? projectId = null,
            string? notebookId = null,
            string? conversationId = null,
            int turnIndex = 0,
            Guid? assistantId = null,
            CancellationToken cancellationToken = default)
        {
            // Create InvocationContext if we have the required context parameters
            InvocationContext? ctx = null;
            if (!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(notebookId) &&
                !string.IsNullOrWhiteSpace(conversationId))
            {
                if (Guid.TryParse(projectId, out var pId) && Guid.TryParse(notebookId, out var nId) &&
                    Guid.TryParse(conversationId, out var cId))
                {
                    ctx = new InvocationContext(pId, nId, cId, chatRunOptions.oAuthUserAccessToken)
                    {
                        TurnIndex = turnIndex,
                        AssistantId = assistantId
                    };
                }
            }

            // Delegate to the new execution engine
            return await ThreadRun.ExecuteAsync(
                chatRunOptions,
                clientFactory,
                previousMessages,
                httpClient,
                messageAdded,
                streamingMessageProgress,
                onExternalToolCall: null,
                resumeWithoutNewUserMessage: false,
                ctx,
                cancellationToken,
                isAgentInvocation: false);
        }
       
    }
}
