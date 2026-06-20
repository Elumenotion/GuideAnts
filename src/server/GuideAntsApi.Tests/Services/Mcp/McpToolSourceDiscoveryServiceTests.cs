using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpOpenApiDescriptorGeneratorTests
{
    [TestMethod]
    public void BuildBridgeServerUrl_UsesClientBridgePrefix()
    {
        McpOpenApiDescriptorGenerator.BuildBridgeServerUrl("my-server")
            .Should().Be("client://mcp-bridge-my-server");
    }

    [TestMethod]
    public void SanitizeOperationId_StabilizesSpecialCharacters()
    {
        McpOpenApiDescriptorGenerator.SanitizeOperationId("search/files", "mcp")
            .Should().Be("mcp_search_files");
    }

    [TestMethod]
    public void ComputeSchemaHash_IsStableForEquivalentJson()
    {
        using var schemaA = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""");
        using var schemaB = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "q": { "type": "string" }
              }
            }
            """);

        var hashA = McpOpenApiDescriptorGenerator.ComputeSchemaHash(schemaA.RootElement);
        var hashB = McpOpenApiDescriptorGenerator.ComputeSchemaHash(schemaB.RootElement);

        hashA.Should().Be(hashB);
        hashA.Should().HaveLength(64);
    }

    [TestMethod]
    public void RedactHeaders_NeverReturnsRawSecrets()
    {
        var redacted = McpOpenApiDescriptorGenerator.RedactHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer secret-token",
            ["X-Empty"] = "",
        });

        redacted["Authorization"].Should().Be("***");
        redacted["X-Empty"].Should().Be("");
    }
}

[TestClass]
public sealed class McpToolSourceDiscoveryServiceTests
{
    private readonly McpToolSourceDiscoveryService _service = new(NullLogger<McpToolSourceDiscoveryService>.Instance);

    [TestMethod]
    public async Task TestConnection_ClientBridge_ValidatesBridgeId()
    {
        var result = await _service.TestConnectionAsync(new McpTestConnectionRequest(
            new McpToolSourceConnectionDto("client_bridge", null, "worm-bridge", null, null)));

        result.Connected.Should().BeTrue();
        result.Message.Should().Contain("Client bridge");
    }

    [TestMethod]
    public async Task TestConnection_ClientBridge_RejectsMissingBridgeId()
    {
        var result = await _service.TestConnectionAsync(new McpTestConnectionRequest(
            new McpToolSourceConnectionDto("client_bridge", null, null, null, null)));

        result.Connected.Should().BeFalse();
    }

    [TestMethod]
    public async Task TestConnection_StreamableHttp_RejectsInvalidUrl()
    {
        var result = await _service.TestConnectionAsync(new McpTestConnectionRequest(
            new McpToolSourceConnectionDto("streamable_http", "not-a-url", null, null, null)));

        result.Connected.Should().BeFalse();
    }

    [TestMethod]
    public async Task DiscoverTools_ClientBridge_RequiresBridgeToolsPayload()
    {
        var result = await _service.DiscoverToolsAsync(new McpDiscoverToolsRequest(
            new McpToolSourceConnectionDto("client_bridge", null, "worm-bridge", null, "mcp"),
            null,
            null));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("client host");
    }

    [TestMethod]
    public async Task DiscoverTools_ClientBridge_ConvertsBridgeToolsWithStableIds()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""");
        var request = new McpDiscoverToolsRequest(
            new McpToolSourceConnectionDto("client_bridge", null, "worm-bridge", null, "mcp"),
            null,
            [
                new McpBridgeToolInputDto("search", "Search", "Search files", schema.RootElement),
            ]);

        var first = await _service.DiscoverToolsAsync(request);
        first.Success.Should().BeTrue();
        first.Tools.Should().ContainSingle();
        first.Tools[0].BackingToolId.Should().Be("search");
        first.Tools[0].OperationId.Should().Be("mcp_search");
        first.Diff.Added.Should().Be(1);

        var second = await _service.DiscoverToolsAsync(request with
        {
            ExistingTools =
            [
                new McpExistingToolStateDto("search", first.Tools[0].SchemaHash, true, "mcp_search"),
            ],
        });

        second.Success.Should().BeTrue();
        second.Tools[0].DiffState.Should().Be("unchanged");
        second.Tools[0].OperationId.Should().Be("mcp_search");
        second.Diff.Added.Should().Be(0);
    }

    [TestMethod]
    public async Task DiscoverTools_ClientBridge_DetectsChangedAndRemovedTools()
    {
        using var oldSchema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"}}}""");
        using var newSchema = JsonDocument.Parse("""{"type":"object","properties":{"q":{"type":"string"},"limit":{"type":"integer"}}}""");

        var existingHash = McpOpenApiDescriptorGenerator.ComputeSchemaHash(oldSchema.RootElement);

        var refresh = await _service.DiscoverToolsAsync(new McpDiscoverToolsRequest(
            new McpToolSourceConnectionDto("client_bridge", null, "worm-bridge", null, null),
            [
                new McpExistingToolStateDto("search", existingHash, true, "search"),
                new McpExistingToolStateDto("delete", existingHash, true, "delete"),
            ],
            [
                new McpBridgeToolInputDto("search", "Search", null, newSchema.RootElement),
            ]));

        refresh.Success.Should().BeTrue();
        refresh.Diff.Changed.Should().Be(1);
        refresh.Diff.Removed.Should().Be(1);
        refresh.Tools.Should().Contain(t => t.BackingToolId == "search" && t.DiffState == "changed");
        refresh.Tools.Should().Contain(t => t.BackingToolId == "delete" && t.DiffState == "removed");
    }
}
