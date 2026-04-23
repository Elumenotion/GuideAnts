using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.Services.Conversations;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public class AttachmentMarkdownChatPolicyTests
{
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Qx0sAAAAASUVORK5CYII=";

    [TestMethod]
    public void ReplaceInlineImageDataUrls_ReplacesDataUrlWithPlaceholder()
    {
        var md = $"![](data:image/png;base64,{TinyPngBase64})";
        var (sanitized, count) = MarkdownInlineImageSanitizer.ReplaceInlineImageDataUrls(md);

        count.Should().Be(1);
        sanitized.Should().Contain(MarkdownInlineImageSanitizer.InlineImagePlaceholder);
        sanitized.Should().NotContain("base64,");
    }

    [TestMethod]
    public void ReplaceInlineImageDataUrls_ReplacesMultipleImages()
    {
        var md =
            $"a ![](data:image/png;base64,{TinyPngBase64}) b ![](data:image/jpeg;base64,{TinyPngBase64}) c";
        var (sanitized, count) = MarkdownInlineImageSanitizer.ReplaceInlineImageDataUrls(md);

        count.Should().Be(2);
        sanitized.Split(MarkdownInlineImageSanitizer.InlineImagePlaceholder).Should().HaveCount(3);
    }

    [TestMethod]
    public void BuildMarkdownAttachmentUserText_UnderCap_IncludesSanitizedMarkdown()
    {
        var body = "Hello world";
        var result = AttachmentMarkdownChatPolicy.BuildMarkdownAttachmentUserText(
            body,
            "doc.pdf",
            "report.pdf",
            maxInlineCharacters: 100_000,
            NullLogger.Instance);

        result.Should().StartWith("Markdown content of doc.pdf:");
        result.Should().Contain("Hello world");
    }

    [TestMethod]
    public void BuildMarkdownAttachmentUserText_OverCap_UsesPathNoticeOnly()
    {
        var filler = new string('x', 50_000);
        var body = $"intro ![](data:image/png;base64,{TinyPngBase64})\n{filler}";
        var result = AttachmentMarkdownChatPolicy.BuildMarkdownAttachmentUserText(
            body,
            "big.pdf",
            "../data/big.pdf",
            maxInlineCharacters: 100,
            NullLogger.Instance);

        result.Should().Contain("too large to include inline");
        result.Should().Contain("`../data/big.pdf`");
        result.Should().NotContain(filler);
    }

    [TestMethod]
    public void BuildMarkdownAttachmentUserText_StripsImagesBeforeLengthCheck()
    {
        var hugeB64 = new string('A', 200_000);
        var body = $"![](data:image/png;base64,{hugeB64})\nshort text";
        var result = AttachmentMarkdownChatPolicy.BuildMarkdownAttachmentUserText(
            body,
            "f.pdf",
            "out.pdf",
            maxInlineCharacters: 500,
            NullLogger.Instance);

        result.Should().StartWith("Markdown content of f.pdf:");
        result.Should().Contain("short text");
        result.Should().Contain(MarkdownInlineImageSanitizer.InlineImagePlaceholder);
        result.Should().NotContain(hugeB64);
    }
}
