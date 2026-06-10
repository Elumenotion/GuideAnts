using FluentAssertions;
using GuideAntsApi.Utils;

namespace GuideAntsApi.Tests.Utils;

[TestClass]
public sealed class MarkdownUrlConverterTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NotebookId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public void ConvertAbsoluteToRelative_ImageUrl_BecomesRelativePath()
    {
        var input = $"![chart](http://localhost:5000/api/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=sub%2Fchart.png)";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be("![chart](./sub/chart.png)");
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_LinkUrl_BecomesRelativePath()
    {
        var input = $"[doc](https://api.server.com/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=notes.md)";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be("[doc](./notes.md)");
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_LeadingSlashPath_IsNormalized()
    {
        var input = $"![x](http://h/api/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=%2Fimages%2Fa.png)";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be("![x](./images/a.png)");
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_PreservesMessageTimestampParam()
    {
        var input = $"![x](http://h/api/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=a.png&m=12345)";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be("![x](./a.png?m=12345)");
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_NonTimestampParam_IsNotAppended()
    {
        var input = $"![x](http://h/api/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=a.png&foo=bar)";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be("![x](./a.png)");
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_DecodesEncodedBackslashesToForwardSlashes()
    {
        var input = $"![x](http://h/api/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=dir%5Cfile.png)";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be("![x](./dir/file.png)");
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_NullOrEmpty_ReturnedUnchanged()
    {
        MarkdownUrlConverter.ConvertAbsoluteToRelative(null!).Should().BeNull();
        MarkdownUrlConverter.ConvertAbsoluteToRelative(string.Empty).Should().BeEmpty();
    }

    [TestMethod]
    public void ConvertAbsoluteToRelative_NonMatchingContent_IsUnchanged()
    {
        const string input = "Just some text with [a normal link](https://example.com/page).";

        var result = MarkdownUrlConverter.ConvertAbsoluteToRelative(input);

        result.Should().Be(input);
    }

    [TestMethod]
    public void ConvertRelativeToAbsolute_RelativePath_BecomesAuthenticatedApiUrl()
    {
        const string input = "![chart](./sub/chart.png)";

        var result = MarkdownUrlConverter.ConvertRelativeToAbsolute(input, ProjectId, NotebookId, "https://api.server.com/");

        result.Should().Be($"![chart](https://api.server.com/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=sub%2fchart.png)");
    }

    [TestMethod]
    public void ConvertRelativeToAbsolute_TrimsTrailingSlashOnBaseUrl()
    {
        const string input = "[doc](notes.md)";

        var result = MarkdownUrlConverter.ConvertRelativeToAbsolute(input, ProjectId, NotebookId, "https://api.server.com///");

        result.Should().StartWith($"[doc](https://api.server.com/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=");
    }

    [TestMethod]
    public void ConvertRelativeToAbsolute_AbsoluteUrls_AreLeftUnchanged()
    {
        const string input = "[ext](https://example.com/page) and [img](http://other.com/x.png)";

        var result = MarkdownUrlConverter.ConvertRelativeToAbsolute(input, ProjectId, NotebookId, "https://api.server.com");

        result.Should().Be(input);
    }

    [TestMethod]
    public void ConvertRelativeToAbsolute_FragmentLink_IsLeftUnchanged()
    {
        const string input = "[jump](#section)";

        var result = MarkdownUrlConverter.ConvertRelativeToAbsolute(input, ProjectId, NotebookId, "https://api.server.com");

        result.Should().Be(input);
    }

    [TestMethod]
    public void ConvertRelativeToAbsolute_NonHttpSchemeWithColonSlashSlash_IsLeftUnchanged()
    {
        const string input = "[ws](ws://host/socket)";

        var result = MarkdownUrlConverter.ConvertRelativeToAbsolute(input, ProjectId, NotebookId, "https://api.server.com");

        result.Should().Be(input);
    }

    [TestMethod]
    public void ConvertRelativeToAbsolute_NullOrEmpty_ReturnedUnchanged()
    {
        MarkdownUrlConverter.ConvertRelativeToAbsolute(null!, ProjectId, NotebookId, "https://x").Should().BeNull();
        MarkdownUrlConverter.ConvertRelativeToAbsolute(string.Empty, ProjectId, NotebookId, "https://x").Should().BeEmpty();
    }

    [TestMethod]
    public void RoundTrip_AbsoluteToRelativeAndBack_PreservesPath()
    {
        var original = $"![chart](http://h/api/projects/{ProjectId}/notebooks/{NotebookId}/files/content?path=sub%2Fchart.png)";

        var relative = MarkdownUrlConverter.ConvertAbsoluteToRelative(original);
        var absolute = MarkdownUrlConverter.ConvertRelativeToAbsolute(relative, ProjectId, NotebookId, "https://api.server.com");

        relative.Should().Be("![chart](./sub/chart.png)");
        absolute.Should().Contain("files/content?path=");
    }
}
