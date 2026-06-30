using FluentAssertions;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpSandboxSetupComposerTests
{
    [TestMethod]
    public void Compose_pypi_package_writes_requirements_line()
    {
        var artifacts = McpSandboxSetupComposer.Compose([
            new McpPackageDescriptorDto("pypi", "mcp-server-example", "uvx", ["mcp-server-example"]),
        ]);

        artifacts.RequirementsText.Should().Contain("mcp-server-example");
        artifacts.AptPackagesText.Should().BeEmpty();
    }

    [TestMethod]
    public void Compose_npm_package_writes_install_script_and_node_apt()
    {
        var artifacts = McpSandboxSetupComposer.Compose([
            new McpPackageDescriptorDto("npm", "@example/mcp", "npx", ["-y", "@example/mcp"]),
        ]);

        artifacts.AptPackagesText.Should().Contain("nodejs");
        artifacts.InstallScriptsJson.Should().Contain("npm install -g @example/mcp");
    }
}

[TestClass]
public sealed class McpSandboxPublishGateServiceTests
{
    [TestMethod]
    public void HasPendingSandboxApply_true_when_requirements_pending()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {
              "overallStatus": "pending",
              "requirements": { "pendingApply": true }
            }
            """);

        McpSandboxPublishGateService.HasPendingSandboxApply(doc.RootElement).Should().BeTrue();
    }

    [TestMethod]
    public void HasPendingSandboxApply_false_when_ready()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {
              "overallStatus": "ready",
              "requirements": { "pendingApply": false },
              "installScripts": { "pendingApply": false }
            }
            """);

        McpSandboxPublishGateService.HasPendingSandboxApply(doc.RootElement).Should().BeFalse();
    }
}

[TestClass]
public sealed class ToolSourceValidatorMcpPrefixTests
{
    [TestMethod]
    public void ValidateMcpAssistantConstraints_detects_duplicate_toolNamePrefix()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "MCP", "version": "1.0.0" },
              "servers": [{ "url": "mcp+api://a" }],
              "x-guideants-tool-source": {
                "kind": "mcp",
                "runtimeExecution": "api",
                "discoveryTransport": "streamable_http",
                "bridgeId": "a",
                "url": "https://example.com/mcp",
                "toolNamePrefix": "mcp"
              },
              "paths": {
                "/tools/search": {
                  "post": {
                    "operationId": "search",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var messages = ToolSourceValidator.ValidateMcpAssistantConstraints([
            ("source-a", spec),
            ("source-b", spec),
        ]);

        messages.Should().ContainSingle(m =>
            m.Code == "duplicate_mcp_tool_name_prefix"
            && m.Message.Contains("toolNamePrefix 'mcp'", StringComparison.Ordinal));
    }
}
