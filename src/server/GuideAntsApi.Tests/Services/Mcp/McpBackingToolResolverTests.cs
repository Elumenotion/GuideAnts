using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpBackingToolResolverTests
{
    [TestMethod]
    public void ResolveBackingToolName_Uses_x_guideants_mcp_tool_metadata_first()
    {
        var schema = ParseSchema("""
            {
              "x-guideants-mcp-tool": {
                "backingToolId": "return_policy"
              }
            }
            """);

        McpBackingToolResolver.ResolveBackingToolName(
                "mcp_stripe_return_policy",
                "/tools/other",
                schema,
                "mcp_stripe")
            .Should().Be("return_policy");
    }

    [TestMethod]
    public void ResolveBackingToolName_Parses_tool_path()
    {
        var schema = ParseSchema("{}");

        McpBackingToolResolver.ResolveBackingToolName(
                "mcp_return_policy",
                "/tools/return_policy",
                schema,
                "mcp")
            .Should().Be("return_policy");
    }

    [TestMethod]
    public void ResolveBackingToolName_Strips_toolNamePrefix_from_operation_id()
    {
        var schema = ParseSchema("{}");

        McpBackingToolResolver.ResolveBackingToolName(
                "mcp_stripe_search",
                "/items",
                schema,
                "mcp_stripe")
            .Should().Be("search");
    }

    private static JsonElement ParseSchema(string json) =>
        JsonDocument.Parse(json).RootElement;
}
