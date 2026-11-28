using AntRunner.Chat;
using AntRunner.Chat.Abstractions;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class MessageComposerTests
{
    private static VisionAttachment Attachment(string path = "data/plot.png", string caption = "A line chart", string tool = "GetImageForInspection")
        => new($"data:image/png;base64,AA", caption, path, tool);

    [TestMethod]
    public void SingleAttachment_ProducesAssistantBridgeThenUserAttachment()
    {
        var (bridge, attachment) = MessageComposer.ComposeAttachmentSequence([Attachment()]);

        bridge.Role.Should().Be(ChatRole.Assistant);
        bridge.IsSynthetic.Should().BeTrue();
        bridge.Content.Should().ContainSingle().Which.IsText.Should().BeTrue();
        bridge.GetText().Should().Contain("GetImageForInspection").And.Contain("retrieved image");
        // the path lives in the attachment caption, not the bridge
        attachment.Content[0].Text!.Should().Contain("data/plot.png");

        attachment.Role.Should().Be(ChatRole.User);
        attachment.IsSynthetic.Should().BeTrue();
        // caption text first, then exactly one image part
        attachment.Content.Should().HaveCount(2);
        attachment.Content[0].IsText.Should().BeTrue();
        attachment.Content[1].IsImage.Should().BeTrue();
        attachment.Content[1].ImageUrl!.Url.Should().StartWith("data:image/png;base64,");
    }

    [TestMethod]
    public void BatchOfTwoAttachments_ShareOneBridgeAndOneUserMessage()
    {
        var (bridge, attachment) = MessageComposer.ComposeAttachmentSequence(
        [
            Attachment("a.png", "first image"),
            Attachment("b.png", "second image", tool: "OtherTool")
        ]);

        bridge.GetText().Should().Contain("2 retrieved images").And.Contain("GetImageForInspection").And.Contain("OtherTool");
        // caption + 2 image parts
        attachment.Content.Should().HaveCount(3);
        attachment.Content.Where(c => c.IsImage).Should().HaveCount(2);
    }

    [TestMethod]
    public void Caption_FramesImagesAsToolOutputNotUserInput()
    {
        var caption = MessageComposer.BuildAttachmentCaption([Attachment(caption: "inspecting plot")]);

        caption.Should().Contain("not new user input");
        caption.Should().Contain("data/plot.png");
        caption.Should().Contain("retrieved by GetImageForInspection");
        caption.Should().Contain("inspecting plot");
    }

    [TestMethod]
    public void EmptyAttachments_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MessageComposer.ComposeAttachmentSequence([]));
    }

    [TestMethod]
    public void SyntheticFactory_SetsFlagsAndExistingConstructorsDefaultToFalse()
    {
        var synthetic = ChatMessage.Synthetic(ChatRole.Assistant, [new ChatContent("hi")]);
        synthetic.IsSynthetic.Should().BeTrue();

        var normal = new ChatMessage(ChatRole.User, "hello");
        normal.IsSynthetic.Should().BeFalse();

        var toolMessage = new ChatMessage("call_1", "fn", [new ChatContent("ok")]);
        toolMessage.IsSynthetic.Should().BeFalse();
        toolMessage.ToolCallId.Should().Be("call_1");
    }

    [TestMethod]
    public void VisionToolResult_HoldsOutputAndAttachments()
    {
        var result = new VisionToolResult
        {
            Output = "tool result text (no image bytes here)",
            Attachments = [Attachment(caption: "why")]
        };

        result.Output.Should().NotContain("base64");
        result.Attachments.Should().ContainSingle().Which.Caption.Should().Be("why");
    }
}
