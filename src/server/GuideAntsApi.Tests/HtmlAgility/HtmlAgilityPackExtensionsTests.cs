using FluentAssertions;
using HtmlAgility;
using HtmlAgilityPack;

namespace GuideAntsApi.Tests.HtmlAgility;

[TestClass]
public sealed class HtmlAgilityPackExtensionsTests
{
    private static HtmlDocument Load(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    [TestMethod]
    public void ConvertToMarkdown_Headings_RenderAtCorrectLevels()
    {
        var doc = Load("<h1>Title</h1><h2>Sub</h2><h3>Third</h3><h4>Fourth</h4><h5>Fifth</h5><h6>Sixth</h6>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("# Title");
        result.Content.Should().Contain("## Sub");
        result.Content.Should().Contain("### Third");
        result.Content.Should().Contain("#### Fourth");
        result.Content.Should().Contain("##### Fifth");
        result.Content.Should().Contain("###### Sixth");
    }

    [TestMethod]
    public void ConvertToMarkdown_Paragraph_WithWhitespaceOnly_IsSkipped()
    {
        var doc = Load("<p>   </p><p>Real text</p>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("Real text");
        // The whitespace-only paragraph should not introduce a stray empty paragraph entry
        result.Content.Trim().Should().StartWith("Real text");
    }

    [TestMethod]
    public void ConvertToMarkdown_Link_WithAbsoluteUrl_IsRenderedAndCollected()
    {
        var doc = Load("""<a href="https://example.com/page">Click here</a>""");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("[Click here](https://example.com/page)");
        result.PageLinks.Should().ContainKey("Click here");
        result.PageLinks["Click here"].Should().Be("https://example.com/page");
    }

    [TestMethod]
    public void ConvertToMarkdown_Link_WithRelativeHref_ResolvesAgainstBaseUri()
    {
        var doc = Load("""<a href="/docs/start">Docs</a>""");

        var result = doc.ConvertToMarkdown(new Uri("https://example.com/root/"));

        result.Content.Should().Contain("[Docs](https://example.com/docs/start)");
        result.PageLinks["Docs"].Should().Be("https://example.com/docs/start");
    }

    [TestMethod]
    public void ConvertToMarkdown_NonNavigableLinks_AreNotCollected()
    {
        var doc = Load("""
            <a href="#section">Anchor</a>
            <a href="mailto:a@b.com">Mail</a>
            <a href="tel:123">Phone</a>
            """);

        var result = doc.ConvertToMarkdown();

        result.PageLinks.Should().BeEmpty();
        result.Content.Should().Contain("[Anchor](#section)");
        result.Content.Should().Contain("[Mail](mailto:a@b.com)");
    }

    [TestMethod]
    public void ConvertToMarkdown_DuplicateLinkSameTarget_IsNotDuplicated()
    {
        var doc = Load("""
            <a href="https://example.com/a">Same</a>
            <a href="https://example.com/a">Same</a>
            """);

        var result = doc.ConvertToMarkdown();

        result.PageLinks.Should().HaveCount(1);
        result.PageLinks["Same"].Should().Be("https://example.com/a");
    }

    [TestMethod]
    public void ConvertToMarkdown_DuplicateLinkTextDifferentTarget_GetsUniqueSuffix()
    {
        var doc = Load("""
            <a href="https://example.com/a">Same</a>
            <a href="https://example.com/b">Same</a>
            """);

        var result = doc.ConvertToMarkdown();

        result.PageLinks.Should().HaveCount(2);
        result.PageLinks["Same"].Should().Be("https://example.com/a");
        result.PageLinks["Same (2)"].Should().Be("https://example.com/b");
    }

    [TestMethod]
    public void ConvertToMarkdown_Image_WithAltAndSrc_RendersAndCollects()
    {
        var doc = Load("""<img src="https://cdn.example.com/x.png" alt="Logo" />""");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("![Logo](https://cdn.example.com/x.png)");
        result.ImageLinks["Logo"].Should().Be("https://cdn.example.com/x.png");
    }

    [TestMethod]
    public void ConvertToMarkdown_Image_WithoutAlt_UsesHashKey()
    {
        var doc = Load("""<img src="https://cdn.example.com/y.png" />""");

        var result = doc.ConvertToMarkdown();

        result.ImageLinks.Should().HaveCount(1);
        var key = result.ImageLinks.Keys.Single();
        key.Should().HaveLength(8);
        result.ImageLinks[key].Should().Be("https://cdn.example.com/y.png");
    }

    [TestMethod]
    public void ConvertToMarkdown_Image_WithDataUri_IsSkipped()
    {
        var doc = Load("""<img src="data:image/png;base64,AAAA" alt="ignored-data" />""");

        var result = doc.ConvertToMarkdown();

        result.ImageLinks.Should().BeEmpty();
        // No src markdown is emitted; instead the alt-only branch is taken
        result.Content.Should().Contain("[Image: ignored-data]");
    }

    [TestMethod]
    public void ConvertToMarkdown_Image_WithAltButNoSrc_RendersDescription()
    {
        var doc = Load("""<img alt="Only alt" />""");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("[Image: Only alt]");
        result.ImageLinks.Should().BeEmpty();
    }

    [TestMethod]
    public void ConvertToMarkdown_UnorderedList_RendersBullets()
    {
        var doc = Load("<ul><li>One</li><li>Two</li></ul>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("* One");
        result.Content.Should().Contain("* Two");
    }

    [TestMethod]
    public void ConvertToMarkdown_OrderedList_RendersNumbers()
    {
        var doc = Load("<ol><li>First</li><li>Second</li></ol>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("1. First");
        result.Content.Should().Contain("2. Second");
    }

    [TestMethod]
    public void ConvertToMarkdown_InlineFormatting_RendersBoldItalicCode()
    {
        var doc = Load("<b>bold</b><strong>strong</strong><i>italic</i><em>emph</em><code>snippet</code>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("**bold**");
        result.Content.Should().Contain("**strong**");
        result.Content.Should().Contain("*italic*");
        result.Content.Should().Contain("*emph*");
        result.Content.Should().Contain("`snippet`");
    }

    [TestMethod]
    public void ConvertToMarkdown_PreBlock_RendersFencedCode()
    {
        var doc = Load("<pre>line1\nline2</pre>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("```");
        result.Content.Should().Contain("line1 line2");
    }

    [TestMethod]
    public void ConvertToMarkdown_Blockquote_PrefixesEachLine()
    {
        var doc = Load("<blockquote>quoted text</blockquote>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("> quoted text");
    }

    [TestMethod]
    public void ConvertToMarkdown_HorizontalRule_RendersDashes()
    {
        var doc = Load("<p>before</p><hr/><p>after</p>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("---");
        result.Content.Should().Contain("before");
        result.Content.Should().Contain("after");
    }

    [TestMethod]
    public void ConvertToMarkdown_ScriptStyleAndHead_AreIgnored()
    {
        var doc = Load("""
            <html><head><title>Title</title><style>.a{color:red}</style></head>
            <body><script>alert('x')</script><p>visible</p></body></html>
            """);

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("visible");
        result.Content.Should().NotContain("alert('x')");
        result.Content.Should().NotContain("color:red");
        result.Content.Should().NotContain("Title");
    }

    [TestMethod]
    public void ConvertToMarkdown_DecodesHtmlEntities()
    {
        var doc = Load("<p>Fish &amp; Chips &lt;tag&gt;</p>");

        var result = doc.ConvertToMarkdown();

        result.Content.Should().Contain("Fish & Chips <tag>");
    }

    [TestMethod]
    public void ConvertToMarkdown_NonPositiveMaxDepth_IsClampedAndStillProduces()
    {
        var doc = Load("<p>content here</p>");

        var result = doc.ConvertToMarkdown(0);

        result.Content.Should().Contain("content here");
    }

    [TestMethod]
    public void ConvertToMarkdown_NullDocument_ReturnsEmptyResult()
    {
        HtmlDocument? doc = null;

        var result = doc!.ConvertToMarkdown();

        result.Content.Should().BeEmpty();
        result.PageLinks.Should().BeEmpty();
        result.ImageLinks.Should().BeEmpty();
    }

    [TestMethod]
    public void ConvertUrlToMarkdownAsync_EmptyUrl_ReturnsError()
    {
        var result = HtmlAgilityPackExtensions.ConvertUrlToMarkdownAsync("   ").GetAwaiter().GetResult();

        result.Content.Should().Be("Error: URL cannot be null or empty.");
    }

    [TestMethod]
    public void ConvertUrlToMarkdownAsync_NonHttpScheme_ReturnsInvalidFormatError()
    {
        var result = HtmlAgilityPackExtensions.ConvertUrlToMarkdownAsync("ftp://example.com/file").GetAwaiter().GetResult();

        result.Content.Should().Contain("Invalid URL format");
    }

    [TestMethod]
    public void ConvertUrlToMarkdown_Sync_InvalidUrl_ReturnsInvalidFormatError()
    {
        var result = HtmlAgilityPackExtensions.ConvertUrlToMarkdown("not-a-valid-url");

        result.Content.Should().Contain("Invalid URL format");
    }
}
