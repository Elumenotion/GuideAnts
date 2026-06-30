using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpDescriptorMigratorTests
{
  private const string LegacyHttpSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "MCP", "version": "1.0.0" },
          "servers": [{ "url": "client://mcp-bridge-worm" }],
          "x-guideants-tool-source": {
            "kind": "mcp",
            "transport": "streamable_http",
            "bridgeId": "worm",
            "url": "https://mcp.example.com/mcp",
            "toolNamePrefix": "mcp_stripe",
            "headers": { "Authorization": "{{secret:MCP_API_KEY}}" }
          },
          "paths": {
            "/tools/search": {
              "post": {
                "operationId": "mcp_stripe_search",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

  private const string LegacyClientBridgeSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "MCP", "version": "1.0.0" },
          "servers": [{ "url": "client://mcp-bridge-local" }],
          "x-guideants-tool-source": {
            "kind": "mcp",
            "transport": "client_bridge",
            "bridgeId": "local",
            "toolNamePrefix": "mcp"
          },
          "paths": {
            "/tools/a": { "post": { "operationId": "a", "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

  private const string LegacySandboxSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "MCP", "version": "1.0.0" },
          "servers": [{ "url": "client://mcp-bridge-pkg" }],
          "x-guideants-tool-source": {
            "kind": "mcp",
            "transport": "client_bridge",
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
  public void NeedsMigration_Detects_legacy_client_bridge_urls()
  {
    McpDescriptorMigrator.NeedsMigration(LegacyHttpSpec).Should().BeTrue();
    McpDescriptorMigrator.NeedsMigration(LegacyClientBridgeSpec).Should().BeTrue();
  }

  [TestMethod]
  public void Migrate_Rewrites_streamable_http_to_mcp_api()
  {
    var migrated = McpDescriptorMigrator.Migrate(LegacyHttpSpec);
    using var doc = System.Text.Json.JsonDocument.Parse(migrated);
    doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()
        .Should().Be("mcp+api://worm");
    migrated.Should().Contain("\"runtimeExecution\": \"api\"");
    migrated.Should().Contain("\"discoveryTransport\": \"streamable_http\"");
    migrated.Should().Contain("\"toolNamePrefix\": \"mcp_stripe\"");
    migrated.Should().Contain("{{secret:MCP_API_KEY}}");
    migrated.Should().NotContain("client://mcp-bridge");
    migrated.Should().NotContain("\"transport\"");
  }

  [TestMethod]
  public void Migrate_Rewrites_client_bridge_without_package_to_api()
  {
    var migrated = McpDescriptorMigrator.Migrate(LegacyClientBridgeSpec);
    using var doc = System.Text.Json.JsonDocument.Parse(migrated);
    doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()
        .Should().Be("mcp+api://local");
    migrated.Should().Contain("\"runtimeExecution\": \"api\"");
    migrated.Should().Contain("\"discoveryTransport\": \"streamable_http\"");
  }

  [TestMethod]
  public void Migrate_Rewrites_package_descriptor_to_mcp_sandbox()
  {
    var migrated = McpDescriptorMigrator.Migrate(LegacySandboxSpec);
    using var doc = System.Text.Json.JsonDocument.Parse(migrated);
    doc.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()
        .Should().Be("mcp+sandbox://pkg");
    migrated.Should().Contain("\"runtimeExecution\": \"sandbox_subprocess\"");
    migrated.Should().Contain("\"discoveryTransport\": \"stdio\"");
    migrated.Should().Contain("@example/mcp-server");
    migrated.Should().Contain("{{secret:EXAMPLE_API_KEY}}");
  }

  [TestMethod]
  public void Migrate_Is_idempotent_for_modern_descriptor()
  {
    var migratedOnce = McpDescriptorMigrator.Migrate(LegacyHttpSpec);
    McpDescriptorMigrator.NeedsMigration(migratedOnce).Should().BeFalse();
    McpDescriptorMigrator.Migrate(migratedOnce).Should().Be(migratedOnce);
  }

  [TestMethod]
  public void BuildMcpServerUrl_Uses_locked_schemes()
  {
    McpOpenApiDescriptorGenerator.BuildMcpServerUrl("abc", "api")
        .Should().Be("mcp+api://abc");
    McpOpenApiDescriptorGenerator.BuildMcpServerUrl("abc", "sandbox_subprocess")
        .Should().Be("mcp+sandbox://abc");
  }
}
