namespace AntRunner.Chat.Abstractions;

/// <summary>
/// Merges every <see cref="ChatRole.System"/> and <see cref="ChatRole.Developer"/> message in a
/// conversation — regardless of position — into a single leading
/// <see cref="ChatRole.System"/> message, joining fragments with a blank line. All other
/// messages keep their order. Providers whose chat template enforces that system content
/// appears exactly once at the start (Qwen3.x raises
/// <c>"System message must be at the beginning."</c> otherwise) need this shaping; providers
/// that accept repeated system messages can skip it by leaving
/// <see cref="ProviderChatBehavior.CombineSystemAndDeveloperMessages"/> unset.
/// </summary>
public static class SystemMessageMerger
{
    public static List<ChatMessage> CombineSystemAndDeveloperMessages(IReadOnlyList<ChatMessage> messages)
    {
        var combined = new List<ChatMessage>(messages.Count);
        var fragments = new List<string>();

        foreach (var message in messages)
        {
            if (message.Role is ChatRole.System or ChatRole.Developer)
            {
                var text = message.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    fragments.Add(text);
                }

                continue;
            }

            combined.Add(message);
        }

        if (fragments.Count > 0)
        {
            combined.Insert(0, new ChatMessage(ChatRole.System, string.Join("\n\n", fragments)));
        }

        return combined;
    }
}
