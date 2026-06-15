using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpPublishedContentUrlRewriterTests
{
    private const string PublicOrigin = "http://localhost:5107";
    private const string InternalOrigin = "http://guideants-webapi-ui:8080";
    private const string PublishedPath =
        "/api/published/projects/9c7121ba-774c-4f48-8257-f6606911d2cf/notebooks/cd942472-3238-4f65-b4fb-3a6654e93e0c/conversations/66581e63-881d-4480-bb7c-af32866c9d11/files/content?path=Runs%2Fduck.png&pubId=01fd0738-5401-49c7-9af7-fffc8ab5aefc";

    [TestMethod]
    public void Rewrite_Replaces_internal_host_in_markdown_image()
    {
        var input = $"![duck]({InternalOrigin}{PublishedPath})";

        var result = McpPublishedContentUrlRewriter.Rewrite(input, PublicOrigin);

        result.Should().Be($"![duck]({PublicOrigin}{PublishedPath})");
    }

    [TestMethod]
    public void Rewrite_Prefixes_path_only_markdown_url_with_public_origin()
    {
        var input = $"![duck]({PublishedPath})";

        var result = McpPublishedContentUrlRewriter.Rewrite(input, PublicOrigin);

        result.Should().Be($"![duck]({PublicOrigin}{PublishedPath})");
    }

    [TestMethod]
    public void Rewrite_Replaces_internal_host_in_markdown_link()
    {
        var input = $"[Full image]({InternalOrigin}{PublishedPath})";

        var result = McpPublishedContentUrlRewriter.Rewrite(input, PublicOrigin);

        result.Should().Be($"[Full image]({PublicOrigin}{PublishedPath})");
    }

    [TestMethod]
    public void Rewrite_Replaces_internal_host_in_html_src_attribute()
    {
        var input = $"<img src=\"{InternalOrigin}{PublishedPath}\" alt=\"duck\" />";

        var result = McpPublishedContentUrlRewriter.Rewrite(input, PublicOrigin);

        result.Should().Be($"<img src=\"{PublicOrigin}{PublishedPath}\" alt=\"duck\" />");
    }

    [TestMethod]
    public void Rewrite_Leaves_non_published_urls_unchanged()
    {
        var input = "![chart](https://example.com/assets/chart.png)";

        var result = McpPublishedContentUrlRewriter.Rewrite(input, PublicOrigin);

        result.Should().Be(input);
    }

    [TestMethod]
    public void Rewrite_Returns_empty_when_content_null()
    {
        McpPublishedContentUrlRewriter.Rewrite(null, PublicOrigin).Should().BeEmpty();
    }

    [TestMethod]
    public void Rewrite_Returns_original_when_public_origin_missing()
    {
        var input = $"![duck]({PublishedPath})";

        McpPublishedContentUrlRewriter.Rewrite(input, null).Should().Be(input);
    }
}
