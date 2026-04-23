
namespace AntRunner.Chat
{
    public class Turn
    {
        public string? AssistantName { get; set; }
        public string? Instructions { get; set; }
        public ChatRunOutput? ChatRunOutput { get; internal set; }
    }
}