namespace GuideAntsApi.Models.Conversations;

public record StreamingEvent(string EventType, string Payload);

public static class StreamingEventTypes
{
    // Core streaming events (existing)
    public const string Token = "token";                    // Content deltas (existing)
    public const string Message = "message";                // Complete messages (existing)
    public const string Usage = "usage";                    // Token usage (existing)
    public const string Complete = "complete";              // Stream end (existing)
    public const string Error = "error";                    // Error events
    public const string Cancelled = "cancelled";            // Stream cancelled
    
    // Message type events (existing)
    public const string UserMessage = "user_message";       // User messages
    public const string AssistantMessage = "assistant_message";   // Assistant responses
    public const string ToolResult = "tool_result";         // Tool execution results
    public const string SystemMessage = "system_message";   // System prompts
    
    // Multi-user collaboration events (NEW)
    public const string ConversationLocked = "conversation_locked";     // Someone started streaming
    public const string ConversationUnlocked = "conversation_unlocked"; // Streaming completed
    public const string UserJoined = "user_joined";                     // User joined conversation
    public const string UserLeft = "user_left";                         // User left conversation
    public const string ConnectionEstablished = "connection_established"; // Observer connection confirmed
    public const string StreamingStarted = "streaming_started";         // Assistant started responding
    public const string StreamingProgress = "streaming_progress";       // Periodic progress for observers
    public const string TurnCreated = "turn_created";                   // New conversation turn started
    public const string TurnRemoved = "turn_removed";                   // Turn was undone/removed
    public const string MessageEdited = "message_edited";               // Message was edited by user

    // Client-handled tool flow (NEW)
    public const string ExternalToolCall = "external_tool_call";        // Client-handled tool call subset
    public const string PendingClientTool = "pending_client_tool";      // Stream ended awaiting client results
} 