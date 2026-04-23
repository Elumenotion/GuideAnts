using System.Reflection;
using System.Text.Json.Nodes;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAI;
using FluentAssertions;
using OpenAI;
using OpenAI.Responses;
using ResponseReasoningContent = OpenAI.Responses.ReasoningContent;
using ResponseTextContent = OpenAI.Responses.TextContent;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class OpenAiResponsesClientTests
{
    [TestMethod]
    public void FromResponse_MapsToolCallsAndThinkingSummary()
    {
        var reasoningContent = new ResponseReasoningContent();
        SetProperty(reasoningContent, "Type", ResponseContentType.ReasoningText);
        SetProperty(reasoningContent, "Text", "Summary line");

        var textContent = new ResponseTextContent("Hello", ResponseContentType.OutputText);

        var message = new Message(Role.Assistant, new IResponseContent[]
        {
            textContent,
            reasoningContent
        });

        var toolCall = new FunctionToolCall("call-1", "do_work", JsonNode.Parse("{\"x\":1}")!);

        var response = new Response();
        SetProperty(response, "Output", new List<IResponseItem> { message, toolCall });

        var tokenDetails = new TokenUsageDetails();
        SetProperty(tokenDetails, "CachedTokens", 1);

        var usage = new TokenUsage();
        SetProperty(usage, "InputTokens", 4);
        SetProperty(usage, "OutputTokens", 7);
        SetProperty(usage, "TotalTokens", 11);
        SetProperty(usage, "InputTokenDetails", tokenDetails);

        SetProperty(response, "Usage", usage);

        var mapped = InvokeFromResponse(response);

        mapped.Choices.Should().HaveCount(1);
        mapped.FirstChoice!.FinishReason.Should().Be("tool_calls");
        mapped.FirstChoice.Message.GetText().Should().Be("Hello");
        mapped.FirstChoice.Message.ToolCalls.Should().NotBeNull();
        mapped.FirstChoice.Message.ToolCalls!.Should().HaveCount(1);
        mapped.FirstChoice.Message.ToolCalls[0].Id.Should().Be("call-1");
        mapped.FirstChoice.Message.ToolCalls[0].Function.Name.Should().Be("do_work");
        mapped.FirstChoice.Message.ThinkingBlocks.Should().NotBeNull();
        mapped.FirstChoice.Message.ThinkingBlocks!.Should().HaveCount(1);
        mapped.FirstChoice.Message.ThinkingBlocks[0].Type.Should().Be("thinking");
        mapped.FirstChoice.Message.ThinkingBlocks[0].Thinking.Should().Be("Summary line");
        mapped.Usage.Should().NotBeNull();
        mapped.Usage!.PromptTokens.Should().Be(4);
        mapped.Usage.CompletionTokens.Should().Be(7);
        mapped.Usage.TotalTokens.Should().Be(11);
        mapped.Usage.PromptTokensDetails!.CachedTokens.Should().Be(1);
    }

    private static ChatCompletionResponse InvokeFromResponse(Response response)
    {
        var mapperType = typeof(OpenAiResponsesClient)
            .GetNestedType("OpenAiResponsesMapper", BindingFlags.NonPublic);

        mapperType.Should().NotBeNull("OpenAiResponsesMapper should exist for mapping responses.");
        var method = mapperType!
            .GetMethod("FromResponse", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("FromResponse should be available for response mapping.");
        return (ChatCompletionResponse)method!.Invoke(null, [response])!;
    }

    private static void SetProperty<TTarget, TValue>(TTarget target, string name, TValue value)
    {
        var prop = typeof(TTarget).GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop.Should().NotBeNull($"Property {name} should exist on {typeof(TTarget).Name}.");
        prop!.SetValue(target, value);
    }
}
