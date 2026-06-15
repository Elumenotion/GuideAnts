using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class PublishedAssistantHistoryBuilderTests
{
    [TestMethod]
    public void FilterMessages_on_switch_keeps_user_and_plain_assistant_text()
    {
        var conv = new NotebookConversation
        {
            Id = Guid.NewGuid(),
            Turns =
            [
                new ConversationTurn { TurnIndex = 1, AssistantName = "Guide" }
            ],
            Messages =
            [
                new NotebookConversationMessage
                {
                    TurnIndex = 1,
                    MessageSequence = 1,
                    Role = ChatRole.User,
                    Content = "hello"
                },
                new NotebookConversationMessage
                {
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Role = ChatRole.Assistant,
                    AssistantName = "Guide",
                    Content = "hi from guide"
                }
            ]
        };

        var filtered = PublishedAssistantHistoryBuilder.FilterMessages(conv, "Researcher", isAssistantSwitch: true);

        filtered.Should().HaveCount(2);
        filtered[0].Role.Should().Be(ChatRole.User);
        filtered[1].Content.Should().Be("hi from guide");
    }

    [TestMethod]
    public void IsAssistantSwitch_true_when_last_turn_differs()
    {
        var conv = new NotebookConversation
        {
            Turns = [new ConversationTurn { TurnIndex = 1, AssistantName = "Guide" }]
        };

        PublishedAssistantHistoryBuilder.IsAssistantSwitch(conv, "Researcher").Should().BeTrue();
        PublishedAssistantHistoryBuilder.IsAssistantSwitch(conv, "Guide").Should().BeFalse();
    }
}
