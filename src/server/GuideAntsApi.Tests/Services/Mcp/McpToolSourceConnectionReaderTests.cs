using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpToolSourceConnectionReaderTests
{
    [TestMethod]
    public void TryReadConnection_Parses_api_metadata_and_header_templates()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "MCP", "version": "1.0.0" },
              "servers": [{ "url": "mcp+api://worm" }],
              "x-guideants-tool-source": {
                "kind": "mcp",
                "runtimeExecution": "api",
                "discoveryTransport": "streamable_http",
                "bridgeId": "worm",
                "url": "https://mcp.example.com/mcp",
                "toolNamePrefix": "mcp_stripe",
                "headers": {
                  "Authorization": "{{secret:MCP_API_KEY}}"
                }
              },
              "paths": {}
            }
            """;

        var connection = McpToolSourceConnectionReader.TryReadConnection(spec);

        connection.Should().NotBeNull();
        connection!.BridgeId.Should().Be("worm");
        connection.Url.Should().Be("https://mcp.example.com/mcp");
        connection.ToolNamePrefix.Should().Be("mcp_stripe");
        connection.HeaderTemplates["Authorization"].Should().Be("{{secret:MCP_API_KEY}}");
    }
}
