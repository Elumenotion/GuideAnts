using FluentAssertions;
using HtmlAgility;
using HtmlAgilityPack;

namespace GuideAntsApi.Tests.HtmlAgility;

[TestClass]
public sealed class HtmlAgilityPackExtensionsTests
{
    [TestMethod]
    public void ConvertToMarkdown_Extracts_headings_links_and_images()
    {
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html>
              <body>
                <h1>Title</h1>
                <p>Hello <strong>world</strong></p>
                <a href="/docs/page">Docs</a>
                <img src="/img/logo.png" alt="Logo" />
              </body>
            </html>
            """);

        var result = document.ConvertToMarkdown(new Uri("https://example.com"));

        result.Content.Should().Contain("# Title");
        result.Content.Should().Contain("Hello");
        result.Content.Should().Contain("world");
        result.PageLinks.Should().ContainKey("Docs");
        result.PageLinks["Docs"].Should().Be("https://example.com/docs/page");
        result.ImageLinks.Values.Should().Contain("https://example.com/img/logo.png");
    }

    [TestMethod]
    public void ConvertToMarkdown_Returns_empty_result_for_missing_document_node()
    {
        var document = new HtmlDocument();

        var result = document.ConvertToMarkdown();

        result.Content.Should().BeEmpty();
        result.PageLinks.Should().BeEmpty();
        result.ImageLinks.Should().BeEmpty();
    }

    [TestMethod]
    public void ConvertToMarkdown_Normalizes_non_positive_max_depth()
    {
        var document = new HtmlDocument();
        document.LoadHtml("<html><body><p>Depth test</p></body></html>");

        var result = document.ConvertToMarkdown(maxDepth: 0);

        result.Content.Should().Contain("Depth test");
    }
}
