using System.Text.Json.Serialization;

namespace AntRunner.Chat.Abstractions;

public sealed class ChatContent
{
    [JsonPropertyName("text")]
    public string? Text { get; }

    [JsonPropertyName("image_url")]
    public ChatImageUrl? ImageUrl { get; }

    [JsonIgnore]
    public bool IsText => !string.IsNullOrEmpty(Text);

    [JsonIgnore]
    public bool IsImage => ImageUrl != null;

    [JsonConstructor]
    public ChatContent(string? text = null, ChatImageUrl? imageUrl = null)
    {
        Text = text;
        ImageUrl = imageUrl;
    }

    public ChatContent(string text) : this(text, null) { }

    public ChatContent(ChatImageUrl imageUrl) : this(null, imageUrl) { }

    public override string ToString() => Text ?? string.Empty;
}
