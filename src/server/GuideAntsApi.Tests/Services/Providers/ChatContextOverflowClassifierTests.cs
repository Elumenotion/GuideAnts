using AntRunner.Chat.Abstractions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class ChatContextOverflowClassifierTests
{
    [TestMethod]
    public void TryClassifyBody_LlamaCppOverflow_DetectsAndExtractsTokenCounts()
    {
        const string body =
            "{\"error\":{\"code\":400,\"message\":\"request (552039 tokens) exceeds the available context size (262144 tokens), try increasing it\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":552039,\"n_ctx\":262144}}";

        var matched = ChatContextOverflowClassifier.TryClassifyBody(400, body, out var promptTokens, out var contextSize);

        matched.Should().BeTrue();
        promptTokens.Should().Be(552039);
        contextSize.Should().Be(262144);
    }

    [TestMethod]
    public void TryClassifyBody_OpenAiOverflow_Detects()
    {
        const string body =
            "{\"error\":{\"message\":\"This model's maximum context length is 128000 tokens.\",\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\"}}";

        ChatContextOverflowClassifier.TryClassifyBody(400, body, out _, out _).Should().BeTrue();
    }

    [TestMethod]
    public void TryClassifyBody_FivexxStatus_NotOverflow()
    {
        const string body = "{\"error\":{\"type\":\"exceed_context_size_error\"}}";

        ChatContextOverflowClassifier.TryClassifyBody(500, body, out _, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryClassifyBody_UnrelatedBadRequest_NotOverflow()
    {
        const string body = "{\"error\":{\"message\":\"invalid tool schema\",\"type\":\"invalid_request_error\"}}";

        ChatContextOverflowClassifier.TryClassifyBody(400, body, out _, out _).Should().BeFalse();
    }

    [TestMethod]
    public void Matches_AnthropicPromptTooLong_DetectsThroughExceptionChain()
    {
        var inner = new InvalidOperationException("prompt is too long: 250000 tokens > 200000 maximum");
        var outer = new Exception("Anthropic request failed", inner);

        ChatContextOverflowClassifier.Matches(outer).Should().BeTrue();
    }

    [TestMethod]
    public void Matches_GeminiTokenCountMessage_Detects()
    {
        var ex = new InvalidOperationException(
            "Google Gemini chat request failed (400): The input token count (1200000) exceeds the maximum number of tokens allowed (1048576).");

        ChatContextOverflowClassifier.Matches(ex).Should().BeTrue();
    }

    [TestMethod]
    public void Matches_AnthropicSdkResponseBodyProperty_DetectsWithoutMarkerInMessage()
    {
        // Mirrors Anthropic.Exceptions.AnthropicBadRequestException: a generic Message with the
        // actual upstream error carried only in a ResponseBody property.
        var ex = new FakeSdkException(
            message: "Response status code does not indicate success: 400 (Bad Request).",
            responseBody: "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 250000 tokens > 200000 maximum\"}}");

        ChatContextOverflowClassifier.Matches(ex).Should().BeTrue();
    }

    [TestMethod]
    public void Matches_ResponseBodyUnrelated_NotOverflow()
    {
        var ex = new FakeSdkException(
            message: "Response status code does not indicate success: 400 (Bad Request).",
            responseBody: "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"unknown field 'foo'\"}}");

        ChatContextOverflowClassifier.Matches(ex).Should().BeFalse();
    }

    [TestMethod]
    public void Matches_GenericFailure_NotOverflow()
    {
        ChatContextOverflowClassifier.Matches(new Exception("connection reset by peer")).Should().BeFalse();
    }

    private sealed class FakeSdkException : Exception
    {
        public FakeSdkException(string message, string responseBody) : base(message)
        {
            ResponseBody = responseBody;
        }

        public string ResponseBody { get; }
    }

    [TestMethod]
    public void Matches_Null_NotOverflow()
    {
        ChatContextOverflowClassifier.Matches(null).Should().BeFalse();
    }
}
