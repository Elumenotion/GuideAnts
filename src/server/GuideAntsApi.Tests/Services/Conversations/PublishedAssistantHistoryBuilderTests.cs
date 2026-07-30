using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Mapping;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class PublishedAssistantHistoryBuilderTests
{
    // FilterMessages / IsAssistantSwitch are pure and do not touch the injected dependencies.
    private static ConversationHistoryBuilder CreateBuilder() =>
        new(null!, null!, null!, null!);

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
                    Role = DataModelChatRole.User,
                    Content = "hello"
                },
                new NotebookConversationMessage
                {
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Role = DataModelChatRole.Assistant,
                    AssistantName = "Guide",
                    Content = "hi from guide"
                }
            ]
        };

        var filtered = CreateBuilder().FilterMessages(conv, "Researcher", isAssistantSwitch: true);

        filtered.Should().HaveCount(2);
        filtered[0].Role.Should().Be(DataModelChatRole.User);
        filtered[1].Content.Should().Be("hi from guide");
    }

    [TestMethod]
    public void IsAssistantSwitch_true_when_last_turn_differs()
    {
        var conv = new NotebookConversation
        {
            Turns = [new ConversationTurn { TurnIndex = 1, AssistantName = "Guide" }]
        };

        var builder = CreateBuilder();
        builder.IsAssistantSwitch(conv, "Researcher").Should().BeTrue();
        builder.IsAssistantSwitch(conv, "Guide").Should().BeFalse();
    }

    [TestMethod]
    public void SplitPublishedClientPrefix_keeps_developer_at_front_and_splits_conversation_replay()
    {
        var clientMessages = new List<ChatMessage>
        {
            new(ChatMessageRole.Developer, "<permissions>"),
            new(ChatMessageRole.User, "<environment_context>"),
            new(ChatMessageRole.User, "Hello"),
            new(ChatMessageRole.Assistant, "Hello! I'm your Creative Guide assistant."),
        };

        ConversationHistoryBuilder.SplitPublishedClientPrefix(
            clientMessages,
            out var leadingDeveloper,
            out var conversationalPrefix);

        leadingDeveloper.Should().ContainSingle()
            .Which.GetText().Should().Be("<permissions>");
        conversationalPrefix.Should().HaveCount(3);
        conversationalPrefix[0].GetText().Should().StartWith("<environment_context>");
        conversationalPrefix[1].GetText().Should().Be("Hello");
        conversationalPrefix[2].GetText().Should().Contain("Creative Guide");
    }

    [TestMethod]
    public void SplitPublishedClientPrefix_returns_empty_lists_when_client_messages_null()
    {
        ConversationHistoryBuilder.SplitPublishedClientPrefix(
            null,
            out var leadingDeveloper,
            out var conversationalPrefix);

        leadingDeveloper.Should().BeEmpty();
        conversationalPrefix.Should().BeEmpty();
    }

    [TestMethod]
    public void IndexToolMessagesByCallId_keeps_latest_when_duplicate_tool_call_ids_exist()
    {
        var callId = "VPdXFuP7bh7TOJTjlUOlXHpTYosVnuE3";
        var hugeId = Guid.NewGuid();
        var abortId = Guid.NewGuid();
        var messages = new List<NotebookConversationMessage>
        {
            new()
            {
                Id = hugeId,
                Role = DataModelChatRole.Tool,
                ToolCallId = callId,
                FunctionName = "QueryData",
                Content = new string('x', 1000),
                MessageSequence = 5,
                Created = DateTime.UtcNow.AddSeconds(-2)
            },
            new()
            {
                Id = abortId,
                Role = DataModelChatRole.Tool,
                ToolCallId = callId,
                FunctionName = "QueryData",
                Content = "[Message aborted due to size restrictions]",
                MessageSequence = 6,
                Created = DateTime.UtcNow.AddSeconds(-1)
            }
        };

        var indexed = ConversationHistoryBuilder.IndexToolMessagesByCallId(messages);

        indexed.Should().ContainSingle();
        indexed[callId].Id.Should().Be(abortId);
        indexed[callId].Content.Should().StartWith("[Message aborted");
    }
}
