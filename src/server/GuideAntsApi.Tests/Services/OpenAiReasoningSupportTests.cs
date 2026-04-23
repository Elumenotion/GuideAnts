using System.Reflection;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.OpenAI;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class OpenAiReasoningSupportTests
{
    [TestMethod]
    public void ToChatRequest_OmitsReasoningEffort_ForGpt41()
    {
        var request = new ChatCompletionRequest(
            [new ChatMessage(ChatRole.User, "hello")],
            model: "gpt-4.1",
            reasoningEffort: "high");

        var mapped = InvokeMapperMethod(
            typeof(OpenAiChatClient),
            "OpenAiMapper",
            "ToChatRequest",
            request);

        GetPropertyValue(mapped, "ReasoningEffort").Should().BeNull();
    }

    [TestMethod]
    public void ToChatRequest_IncludesReasoningEffort_ForO3()
    {
        var request = new ChatCompletionRequest(
            [new ChatMessage(ChatRole.User, "hello")],
            model: "o3",
            reasoningEffort: "high");

        var mapped = InvokeMapperMethod(
            typeof(OpenAiChatClient),
            "OpenAiMapper",
            "ToChatRequest",
            request);

        GetPropertyValue(mapped, "ReasoningEffort").Should().NotBeNull();
    }

    [TestMethod]
    public void ToResponseRequest_OmitsReasoning_ForGpt41()
    {
        var request = new ChatCompletionRequest(
            [new ChatMessage(ChatRole.User, "hello")],
            model: "gpt-4.1",
            reasoningEffort: "high");

        var mapped = InvokeMapperMethod(
            typeof(OpenAiResponsesClient),
            "OpenAiResponsesMapper",
            "ToResponseRequest",
            request);

        GetPropertyValue(mapped, "Reasoning").Should().BeNull();
    }

    [TestMethod]
    public void ToResponseRequest_IncludesReasoning_ForO3()
    {
        var request = new ChatCompletionRequest(
            [new ChatMessage(ChatRole.User, "hello")],
            model: "o3",
            reasoningEffort: "high");

        var mapped = InvokeMapperMethod(
            typeof(OpenAiResponsesClient),
            "OpenAiResponsesMapper",
            "ToResponseRequest",
            request);

        GetPropertyValue(mapped, "Reasoning").Should().NotBeNull();
    }

    private static object InvokeMapperMethod(
        Type outerType,
        string nestedTypeName,
        string methodName,
        ChatCompletionRequest request)
    {
        var mapperType = outerType.GetNestedType(nestedTypeName, BindingFlags.NonPublic);
        mapperType.Should().NotBeNull($"{nestedTypeName} should exist.");

        var method = mapperType!.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull($"{methodName} should exist.");

        return method!.Invoke(null, [request])!;
    }

    private static object? GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        property.Should().NotBeNull($"Property {propertyName} should exist on {target.GetType().Name}.");
        return property!.GetValue(target);
    }
}
