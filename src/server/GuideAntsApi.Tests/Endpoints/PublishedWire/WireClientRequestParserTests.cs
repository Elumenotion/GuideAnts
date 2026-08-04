using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints.PublishedWire;

namespace GuideAntsApi.Tests.Endpoints.PublishedWire;

[TestClass]
public sealed class WireClientRequestParserTests
{
    [TestMethod]
    public void BuildOpenAiChatClientPrompt_Extracts_image_urls_from_user_turn()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              {
                "role": "user",
                "content": [
                  { "type": "text", "text": "describe this plantuml" },
                  { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
                ]
              }
            ]
            """);

        var prompt = WireClientRequestParser.BuildOpenAiChatClientPrompt(doc.RootElement);

        prompt.UserPrompt.Should().Be("describe this plantuml");
        prompt.UserImageUrls.Should().ContainSingle("data:image/png;base64,AAAA");
        prompt.PrefixMessages.Should().BeEmpty();
    }

    [TestMethod]
    public void BuildOpenAiChatClientPrompt_Preserves_string_image_url_shape()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              {
                "role": "user",
                "content": [
                  { "type": "text", "text": "what is this?" },
                  { "type": "image_url", "image_url": "https://example.com/a.png" }
                ]
              }
            ]
            """);

        var prompt = WireClientRequestParser.BuildOpenAiChatClientPrompt(doc.RootElement);

        prompt.UserImageUrls.Should().ContainSingle("https://example.com/a.png");
    }

    [TestMethod]
    public void BuildOpenAiChatClientPrompt_Keeps_prior_multimodal_messages_in_prefix()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              {
                "role": "user",
                "content": [
                  { "type": "text", "text": "first" },
                  { "type": "image_url", "image_url": { "url": "data:image/png;base64,BBBB" } }
                ]
              },
              { "role": "assistant", "content": "ok" },
              { "role": "user", "content": "follow up" }
            ]
            """);

        var prompt = WireClientRequestParser.BuildOpenAiChatClientPrompt(doc.RootElement);

        prompt.UserPrompt.Should().Be("follow up");
        prompt.UserImageUrls.Should().BeEmpty();
        prompt.PrefixMessages.Should().HaveCount(2);
        prompt.PrefixMessages[0].Content.Should().Contain(c =>
            c.IsImage && c.ImageUrl!.Url == "data:image/png;base64,BBBB");
    }

    [TestMethod]
    public void BuildAnthropicClientPrompt_Extracts_base64_image_urls()
    {
        using var system = JsonDocument.Parse("\"\"");
        using var messages = JsonDocument.Parse(
            """
            [
              {
                "role": "user",
                "content": [
                  { "type": "text", "text": "look" },
                  {
                    "type": "image",
                    "source": {
                      "type": "base64",
                      "media_type": "image/jpeg",
                      "data": "ZZZZ"
                    }
                  }
                ]
              }
            ]
            """);

        var prompt = WireClientRequestParser.BuildAnthropicClientPrompt(system.RootElement, messages.RootElement);

        prompt.UserPrompt.Should().Be("look");
        prompt.UserImageUrls.Should().ContainSingle("data:image/jpeg;base64,ZZZZ");
    }

    [TestMethod]
    public void BuildOpenAiResponsesClientPrompt_Extracts_input_image_urls()
    {
        using var doc = JsonDocument.Parse(
            """
            [
              {
                "role": "user",
                "content": [
                  { "type": "input_text", "text": "caption this" },
                  { "type": "input_image", "image_url": "data:image/png;base64,CCCC" }
                ]
              }
            ]
            """);

        var prompt = WireClientRequestParser.BuildOpenAiResponsesClientPrompt(doc.RootElement);

        prompt.UserPrompt.Should().Be("caption this");
        prompt.UserImageUrls.Should().ContainSingle("data:image/png;base64,CCCC");
    }
}
