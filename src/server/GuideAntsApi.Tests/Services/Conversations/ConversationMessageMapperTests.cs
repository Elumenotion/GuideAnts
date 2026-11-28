using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Mapping;
using System.Text.Json;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class ConversationMessageMapperTests
{
    [TestMethod]
    public void FilterDuplicateAssistantMessages_DropsToollessDuplicateOfToolCallMessage()
    {
        var withTools = new NotebookConversationMessage
        {
            Role = DataModelChatRole.Assistant,
            Content = "Doing work",
            ToolCalls = """[{"id":"call_1"}]""",
            TurnIndex = 0
        };
        var toollessDuplicate = new NotebookConversationMessage
        {
            Role = DataModelChatRole.Assistant,
            Content = "Doing work",
            ToolCalls = null,
            TurnIndex = 0
        };
        var unrelated = new NotebookConversationMessage
        {
            Role = DataModelChatRole.User,
            Content = "Hello",
            TurnIndex = 0
        };

        var input = new List<NotebookConversationMessage> { withTools, toollessDuplicate, unrelated };

        var result = ConversationMessageMapper.FilterDuplicateAssistantMessages(input);

        result.Should().Contain(withTools);
        result.Should().Contain(unrelated);
        result.Should().NotContain(toollessDuplicate);
    }

    [TestMethod]
    public void FormatThinkingDisplay_ReturnsThinkingText_WhenBlockIsThinking()
    {
        var block = ChatThinkingBlock.ForThinking("Let me reason this through.", "sig");

        ConversationMessageMapper.FormatThinkingDisplay(block).Should().Be("Let me reason this through.");
    }

    [TestMethod]
    public void FormatThinkingDisplay_ReturnsRedactedLabel_WhenBlockIsRedacted()
    {
        var block = ChatThinkingBlock.ForRedacted("redacted-data");

        ConversationMessageMapper.FormatThinkingDisplay(block).Should().Be("Thinking (redacted)");
    }

    [TestMethod]
    public void HasVisibleAssistantBody_IsFalseForEmptyAssistantWithoutToolsOrAttachments()
    {
        ConversationMessageMapper.HasVisibleAssistantBody(DataModelChatRole.Assistant, "", false, 0).Should().BeFalse();
        ConversationMessageMapper.HasVisibleAssistantBody(DataModelChatRole.Assistant, "   ", false, 0).Should().BeFalse();
        ConversationMessageMapper.HasVisibleAssistantBody(DataModelChatRole.Assistant, "reply", false, 0).Should().BeTrue();
        ConversationMessageMapper.HasVisibleAssistantBody(DataModelChatRole.Assistant, "", true, 0).Should().BeTrue();
        ConversationMessageMapper.HasVisibleAssistantBody(DataModelChatRole.Assistant, "", false, 1).Should().BeTrue();
        ConversationMessageMapper.HasVisibleAssistantBody(DataModelChatRole.User, "", false, 0).Should().BeTrue();
    }

    [TestMethod]
    public void ToConversationDto_OmitsEmptyAssistantContent_ButKeepsThinking()
    {
        var assistantId = Guid.NewGuid();
        var thinkingJson = JsonSerializer.Serialize(
            new[] { ChatThinkingBlock.ForThinking("where is the hosted file name", string.Empty) },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var conversation = new NotebookConversation
        {
            NotebookId = Guid.NewGuid(),
            Created = DateTime.UtcNow,
            Messages =
            [
                new NotebookConversationMessage
                {
                    Id = Guid.NewGuid(),
                    Role = DataModelChatRole.User,
                    Content = "stop",
                    TurnIndex = 1,
                    MessageSequence = 1,
                    Created = DateTime.UtcNow,
                    IsStreaming = false
                },
                new NotebookConversationMessage
                {
                    Id = assistantId,
                    Role = DataModelChatRole.Assistant,
                    Content = "",
                    ThinkingBlocksJson = thinkingJson,
                    TurnIndex = 1,
                    MessageSequence = 2,
                    Created = DateTime.UtcNow,
                    IsStreaming = false
                }
            ]
        };

        var dto = ConversationMessageMapper.ToConversationDto(conversation);

        dto.Messages.Should().Contain(m => m.Content == "stop");
        dto.Messages.Should().Contain(m => m.Content == "where is the hosted file name");
        dto.Messages.Should().NotContain(m => m.Id == assistantId);
    }

    [TestMethod]
    public void ToConversationDto_ExcludesSyntheticRunLoopMessages()
    {
        // The vision-tool sequence persists a synthetic assistant bridge and a synthetic
        // user attachment message. Both are real model context (replayed on continued
        // turns) but must never appear in the user-facing transcript.
        var realUser = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            Role = DataModelChatRole.User,
            Content = "inspect the number png",
            TurnIndex = 1,
            MessageSequence = 1,
            Created = DateTime.UtcNow,
            IsStreaming = false
        };
        var bridge = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            Role = DataModelChatRole.Assistant,
            Content = "Presenting the retrieved image as the output of GetImageForInspection for inspection.",
            TurnIndex = 1,
            MessageSequence = 3,
            Created = DateTime.UtcNow,
            IsStreaming = false,
            IsSynthetic = true
        };
        var attachment = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            Role = DataModelChatRole.User,
            Content = "[The following image(s) were produced by the tool call(s) immediately above...]",
            TurnIndex = 1,
            MessageSequence = 4,
            Created = DateTime.UtcNow,
            IsStreaming = false,
            IsSynthetic = true
        };
        var finalAssistant = new NotebookConversationMessage
        {
            Id = Guid.NewGuid(),
            Role = DataModelChatRole.Assistant,
            Content = "The number in the image is 8446.",
            TurnIndex = 1,
            MessageSequence = 5,
            Created = DateTime.UtcNow,
            IsStreaming = false
        };

        var conversation = new NotebookConversation
        {
            NotebookId = Guid.NewGuid(),
            Created = DateTime.UtcNow,
            Messages = [realUser, bridge, attachment, finalAssistant]
        };

        var dto = ConversationMessageMapper.ToConversationDto(conversation);

        dto.Messages.Should().ContainSingle(m => m.Id == realUser.Id);
        dto.Messages.Should().ContainSingle(m => m.Id == finalAssistant.Id);
        dto.Messages.Should().NotContain(m => m.Id == bridge.Id);
        dto.Messages.Should().NotContain(m => m.Id == attachment.Id);
    }
}
