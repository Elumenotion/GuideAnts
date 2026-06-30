using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpSandboxConnectionReaderTests
{
    private const string SandboxSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "MCP", "version": "1.0.0" },
          "servers": [{ "url": "mcp+sandbox://pkg" }],
          "x-guideants-tool-source": {
            "kind": "mcp",
            "runtimeExecution": "sandbox_subprocess",
            "discoveryTransport": "stdio",
            "bridgeId": "pkg",
            "toolNamePrefix": "mcp_github",
            "package": {
              "registryType": "npm",
              "identifier": "@example/mcp-server",
              "command": "npx",
              "args": ["-y", "@example/mcp-server"]
            },
            "environmentVariables": [
              { "name": "EXAMPLE_API_KEY", "secretRef": "{{secret:EXAMPLE_API_KEY}}" }
            ]
          },
          "paths": {
            "/tools/run": { "post": { "operationId": "run", "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    [TestMethod]
    public void TryReadConnection_Parses_sandbox_package_metadata()
    {
        var connection = McpSandboxConnectionReader.TryReadConnection(SandboxSpec);
        connection.Should().NotBeNull();
        connection!.BridgeId.Should().Be("pkg");
        connection.Package.Command.Should().Be("npx");
        connection.Package.Args.Should().BeEquivalentTo(["-y", "@example/mcp-server"]);
        connection.EnvironmentVariableRefs.Should().ContainSingle()
            .Which.Should().Be(new McpEnvironmentVariableRefDto("EXAMPLE_API_KEY", "{{secret:EXAMPLE_API_KEY}}"));
        connection.ToolNamePrefix.Should().Be("mcp_github");
    }

    [TestMethod]
    public void TryReadConnection_Rejects_api_runtime()
    {
        var apiSpec = SandboxSpec.Replace("sandbox_subprocess", "api");
        McpSandboxConnectionReader.TryReadConnection(apiSpec).Should().BeNull();
    }
}
