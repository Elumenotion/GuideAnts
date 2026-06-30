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
    public void BuildMcpServerUrl_UsesLockedSchemes()
    {
        McpOpenApiDescriptorGenerator.BuildMcpServerUrl("my-server", McpRuntimeExecution.Api)
            .Should().Be("mcp+api://my-server");
        McpOpenApiDescriptorGenerator.BuildMcpServerUrl("my-server", McpRuntimeExecution.SandboxSubprocess)
            .Should().Be("mcp+sandbox://my-server");
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
    private readonly McpToolSourceDiscoveryService _service = new(
        new NoOpSandboxStdioDiscoveryClient(),
        NullLogger<McpToolSourceDiscoveryService>.Instance);

    private static McpToolSourceConnectionDto ApiConnection(
        string bridgeId = "worm-bridge",
        string url = "https://mcp.example.com/mcp") =>
        new(
            McpRuntimeExecution.Api,
            McpDiscoveryTransport.StreamableHttp,
            url,
            bridgeId,
            null,
            "mcp");

    private static McpToolSourceConnectionDto SandboxConnection() =>
        new(
            McpRuntimeExecution.SandboxSubprocess,
            McpDiscoveryTransport.Stdio,
            null,
            "worm-bridge",
            null,
            "mcp",
            new McpPackageDescriptorDto("npm", "@example/mcp", "npx", ["-y", "@example/mcp"]));

    [TestMethod]
    public async Task TestConnection_Api_RejectsMissingBridgeId()
    {
        var result = await _service.TestConnectionAsync(new McpTestConnectionRequest(
            ApiConnection(bridgeId: "")));

        result.Connected.Should().BeFalse();
    }

    [TestMethod]
    public async Task TestConnection_Api_RejectsInvalidUrl()
    {
        var result = await _service.TestConnectionAsync(new McpTestConnectionRequest(
            ApiConnection(url: "not-a-url")));

        result.Connected.Should().BeFalse();
    }

    [TestMethod]
    public async Task TestConnection_SandboxSubprocess_RequiresGuideScope()
    {
        var result = await _service.TestConnectionAsync(new McpTestConnectionRequest(
            SandboxConnection()));

        result.Connected.Should().BeFalse();
        result.Message.Should().Contain("projectId");
    }

    [TestMethod]
    public async Task DiscoverTools_SandboxSubprocess_RequiresGuideScope()
    {
        var result = await _service.DiscoverToolsAsync(new McpDiscoverToolsRequest(
            SandboxConnection(),
            null,
            null));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("projectId");
    }

    [TestMethod]
    public async Task DiscoverTools_Rejects_runtime_transport_mismatch()
    {
        var result = await _service.DiscoverToolsAsync(new McpDiscoverToolsRequest(
            new McpToolSourceConnectionDto(
                McpRuntimeExecution.Api,
                McpDiscoveryTransport.Stdio,
                "https://mcp.example.com",
                "worm-bridge",
                null,
                "mcp"),
            null,
            null));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("discoveryTransport");
    }

    private sealed class NoOpSandboxStdioDiscoveryClient : IMcpSandboxStdioDiscoveryClient
    {
        public Task<McpStdioDiscoverResponse> DiscoverAsync(
            Guid projectId,
            Guid guideId,
            McpToolSourceConnectionDto connection,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(McpStdioDiscoverResponse.Succeeded("test-server", "1.0.0", []));
    }
}
