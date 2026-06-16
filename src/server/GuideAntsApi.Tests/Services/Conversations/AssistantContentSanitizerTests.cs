using FluentAssertions;
using GuideAntsApi.Services.Conversations.Streaming;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class AssistantContentSanitizerTests
{
    private static ConversationFileUrlContext CreatePublishedContext() =>
        new(
            ProjectId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            NotebookId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ConversationId: Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            PublisherId: "pub-123",
            HostUrl: "https://example.test");

    [TestMethod]
    public void ConvertSandboxUrlsToRelative_RewritesMarkdownSandboxLinks()
    {
        const string input = "See ![chart](sandbox:/app/Output/chart.png) for details.";

        var result = AssistantContentSanitizer.ConvertSandboxUrlsToRelative(input);

        result.Should().Be("See ![chart](./Output/chart.png) for details.");
    }

    [TestMethod]
    public void ConvertSandboxUrlsToPublished_RewritesMarkdownSandboxLinksToPublishedApiUrls()
    {
        const string input = "See ![chart](sandbox:/app/Output/chart.png) for details.";
        var ctx = CreatePublishedContext();

        var result = AssistantContentSanitizer.ConvertSandboxUrlsToPublished(input, ctx);

        result.Should().Contain("/api/published/projects/");
        result.Should().Contain("pubId=pub-123");
        result.Should().Contain("path=Output%2Fchart.png");
    }

    [TestMethod]
    public void ExtractPrivateFilenameUrlMapFromToolMessage_BuildsFilenameToAbsoluteUrlMap()
    {
        const string toolOutput = """
            New Files
            ---
            File: Output/chart.png
            """;
        var ctx = new ConversationFileUrlContext(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            null,
            "https://example.test");

        var map = AssistantContentSanitizer.ExtractPrivateFilenameUrlMapFromToolMessage(toolOutput, ctx);

        map.Should().ContainKey("chart.png");
        map["chart.png"].Should().Contain("/api/projects/");
        map["chart.png"].Should().Contain("path=Output%2Fchart.png");
    }

    [TestMethod]
    public void ExtractPublishedFilenamePathMapFromToolMessage_BuildsFilenameToRelativePathMap()
    {
        const string toolOutput = """
            New Files
            ---
            File: Output/report.md
            """;

        var map = AssistantContentSanitizer.ExtractPublishedFilenamePathMapFromToolMessage(toolOutput);

        map.Should().ContainKey("report.md");
        map["report.md"].Should().Be("Output/report.md");
    }

    [TestMethod]
    public void SanitizePrivateAssistantContent_RewritesFilenameUrlsFromToolMap()
    {
        var map = new Dictionary<string, string>
        {
            ["chart.png"] = "https://example.test/api/projects/p/n/files/content?path=Output/chart.png"
        };

        const string content = "See ![chart](sandbox:/app/Output/chart.png).";

        var result = AssistantContentSanitizer.SanitizePrivateAssistantContent(content, map, "https://example.test");

        result.Should().Contain("chart.png");
        result.Should().NotContain("sandbox:");
    }

    [TestMethod]
    public void SanitizePublishedAssistantContent_RewritesFilenameUrlsFromToolMap()
    {
        var ctx = CreatePublishedContext();
        var map = new Dictionary<string, string>
        {
            ["chart.png"] = AssistantContentSanitizer.BuildPublishedFileUrl(ctx, "Output/chart.png")
        };

        const string content = "See ![chart](sandbox:/app/Output/chart.png).";

        var result = AssistantContentSanitizer.SanitizePublishedAssistantContent(content, map, ctx);

        result.Should().Contain("/api/published/projects/");
        result.Should().Contain("pubId=pub-123");
        result.Should().NotContain("sandbox:");
    }

    [TestMethod]
    public void AppendQueryParamIfMissing_AddsQueryParam_WhenAbsent()
    {
        const string url = "/api/files/content?path=foo.png";

        var result = AssistantContentSanitizer.AppendQueryParamIfMissing(url, "m", "12345");

        result.Should().Be("/api/files/content?path=foo.png&m=12345");
    }

    [TestMethod]
    public void AppendQueryParamIfMissing_LeavesUrlUnchanged_WhenParamAlreadyPresent()
    {
        const string url = "/api/files/content?path=foo.png&m=999";

        var result = AssistantContentSanitizer.AppendQueryParamIfMissing(url, "m", "12345");

        result.Should().Be(url);
    }
}
