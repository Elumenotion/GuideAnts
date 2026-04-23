using AntRunner.ToolCalling.Functions;
using AntRunner.Chat.Abstractions;
using System.Collections.Concurrent;

namespace AntRunner.Chat
{
    public static class ChatRunnerUtils
    {
        internal static readonly ConcurrentDictionary<string, Dictionary<string, ToolCaller>> RequestBuilderCache = new();

        public static ChatRunOutput? BuildRunResults(List<ChatMessage> messages, ChatCompletionResponse response)
        {
            ChatRunOutput? runResults = new() { Messages = messages };
            // Handle Content as string
            runResults.LastMessage = messages.Last().GetText();
            var choice = response.FirstChoice;
            runResults.Status = choice?.FinishReason ?? "unknown";
            foreach (var message in messages)
            {
                if (message.Role == ChatRole.System || message.Role == ChatRole.Developer) continue;
                if (message.Role == ChatRole.User)
                {
                    string userMessage = message.GetText();
                    runResults.ConversationMessages.Add(new() { Message = userMessage, MessageType = ThreadConversationMessageType.User });
                }
                else if (message.Role == ChatRole.Assistant)
                {
                    string messageText = "";
                    if (!string.IsNullOrEmpty(message.GetText()))
                    {
                        messageText += $"{message.GetText()}\n";
                    }
                    // Tool calls are preserved as structured data in the message object
                    // Don't format them into text here
                    runResults.ConversationMessages.Add(new() { Message = messageText, MessageType = ThreadConversationMessageType.Assistant });
                }
                else if (message.Role == ChatRole.Tool)
                {
                    // Return raw tool result without formatting
                    runResults.ConversationMessages.Add(new() { Message = message.GetText(), MessageType = ThreadConversationMessageType.Tool });
                }
            }

            runResults.Usage = new()
            {
                CompletionTokens = response.Usage?.CompletionTokens ?? 0,
                PromptTokens = response.Usage?.PromptTokens ?? 0,
                CachedPromptTokens = response.Usage?.PromptTokensDetails?.CachedTokens ?? 0,
                TotalTokens = response.Usage?.TotalTokens ?? 0
            };

            return runResults;
        }
        public static JsonElement FilterJsonBySchema(JsonElement content, JsonElement schema)
        {
            var filteredContent = new Dictionary<string, object>();

            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                if (content.TryGetProperty(property.Name, out var value))
                {
                    var type = property.Value.GetProperty("type").GetString();

                    if (type == "object" && property.Value.TryGetProperty("properties", out var nestedSchema))
                    {
                        filteredContent[property.Name] = FilterJsonBySchema(value, property.Value);
                    }
                    else if (type == "array" && property.Value.TryGetProperty("items", out var itemSchema))
                    {
                        var filteredArray = new List<object>();

                        foreach (var item in value.EnumerateArray())
                        {
                            filteredArray.Add(FilterJsonBySchema(item, itemSchema));
                        }

                        filteredContent[property.Name] = filteredArray;
                    }
                    else
                    {
                        filteredContent[property.Name] = value;
                    }
                }
            }

            var jsonString = JsonSerializer.Serialize(filteredContent);
            return JsonDocument.Parse(jsonString).RootElement;
        }
    }
}