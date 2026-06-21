using FluentAssertions;
using GuideAntsApi.Services.Guides;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class ToolSourceValidatorTests
{
    private const string WebSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Web API", "version": "1.0.0" },
          "servers": [{ "url": "https://api.example.com" }],
          "paths": {
            "/items": {
              "get": {
                "operationId": "listItems",
                "summary": "List items",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string ClientSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Client Actions", "version": "1.0.0" },
          "servers": [{ "url": "client://worm-commander-client" }],
          "x-guideants-tool-source": { "kind": "client-actions" },
          "paths": {
            "/start": {
              "post": {
                "operationId": "start",
                "summary": "Start",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string SandboxSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Sandbox", "version": "1.0.0" },
          "servers": [{ "url": "sandbox://__init__.py" }],
          "x-guideants-tool-source": { "kind": "sandbox-module" },
          "paths": {
            "/run": {
              "post": {
                "operationId": "run",
                "summary": "Run",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string ToolSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Local", "version": "1.0.0" },
          "servers": [{ "url": "tool://localhost" }],
          "x-guideants-tool-source": { "kind": "local-function" },
          "paths": {
            "/MyType/MyMethod": {
              "post": {
                "operationId": "myMethod",
                "summary": "Invoke",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string McpClientBridgeSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "MCP", "version": "1.0.0" },
          "servers": [{ "url": "client://mcp-bridge-worm" }],
          "x-guideants-tool-source": {
            "kind": "mcp",
            "transport": "client_bridge",
            "bridgeId": "worm"
          },
          "paths": {
            "/tools/search": {
              "post": {
                "operationId": "search",
                "summary": "Search",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string OperationFragment = """
        {
          "path": "/items",
          "method": "get",
          "operation": {
            "operationId": "listItems",
            "summary": "List items",
            "responses": { "200": { "description": "ok" } }
          }
        }
        """;

    [TestMethod]
    public void ValidateDescriptor_Accepts_web_client_sandbox_tool_and_mcp_specs()
    {
        ToolSourceValidator.ValidateDescriptor(WebSpec, publishChecks: true)
            .Should().NotContain(m => m.Severity == "error");
        ToolSourceValidator.ValidateDescriptor(ClientSpec, publishChecks: true)
            .Should().NotContain(m => m.Severity == "error");
        ToolSourceValidator.ValidateDescriptor(SandboxSpec, publishChecks: true)
            .Should().NotContain(m => m.Severity == "error");
        ToolSourceValidator.ValidateDescriptor(ToolSpec, publishChecks: true)
            .Should().NotContain(m => m.Severity == "error");
        ToolSourceValidator.ValidateDescriptor(McpClientBridgeSpec, publishChecks: true)
            .Should().NotContain(m => m.Severity == "error");
    }

    [TestMethod]
    public void ValidateDescriptor_Rejects_missing_required_fields()
    {
        var messages = ToolSourceValidator.ValidateDescriptor("{}", publishChecks: false);

        messages.Should().Contain(m => m.Code == "missing_openapi_version" && m.Severity == "error");
        messages.Should().Contain(m => m.Code == "missing_info_title");
        messages.Should().Contain(m => m.Code == "missing_servers");
        messages.Should().Contain(m => m.Code == "missing_paths");
    }

    [TestMethod]
    public void ValidateDescriptor_Rejects_duplicate_operation_ids()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Dup", "version": "1.0.0" },
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/a": { "get": { "operationId": "same", "responses": { "200": { "description": "ok" } } } },
                "/b": { "get": { "operationId": "same", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        ToolSourceValidator.ValidateDescriptor(spec, publishChecks: false)
            .Should().Contain(m => m.Code == "duplicate_operation_id" && m.Severity == "error");
    }

    [TestMethod]
    public void ValidateDescriptor_Rejects_source_kind_scheme_mismatch()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Mismatch", "version": "1.0.0" },
              "servers": [{ "url": "https://api.example.com" }],
              "x-guideants-tool-source": { "kind": "sandbox-module" },
              "paths": {
                "/x": { "get": { "operationId": "x", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        ToolSourceValidator.ValidateDescriptor(spec, publishChecks: false)
            .Should().Contain(m => m.Code == "source_kind_scheme_mismatch" && m.Severity == "error");
    }

    [TestMethod]
    public void ValidateDescriptor_Rejects_unsupported_mcp_transport_at_publish()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "MCP SSE", "version": "1.0.0" },
              "servers": [{ "url": "client://mcp-bridge-x" }],
              "x-guideants-tool-source": { "kind": "mcp", "transport": "sse", "bridgeId": "x" },
              "paths": {
                "/tools/a": { "post": { "operationId": "a", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        ToolSourceValidator.ValidateDescriptor(spec, publishChecks: true)
            .Should().Contain(m => m.Code == "unsupported_mcp_transport" && m.Severity == "error");
    }

    [TestMethod]
    public void ValidateDescriptor_Rejects_mcp_scheme_at_publish()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "MCP URL", "version": "1.0.0" },
              "servers": [{ "url": "mcp://my-server" }],
              "x-guideants-tool-source": { "kind": "mcp", "transport": "streamable_http", "url": "https://mcp.example.com" },
              "paths": {
                "/tools/a": { "post": { "operationId": "a", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        ToolSourceValidator.ValidateDescriptor(spec, publishChecks: true)
            .Should().Contain(m => m.Code == "unsupported_mcp_scheme" && m.Severity == "error");
    }

    [TestMethod]
    public void ValidateDescriptor_Rejects_streamable_http_mcp_without_url_at_publish()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "MCP HTTP", "version": "1.0.0" },
              "servers": [{ "url": "client://mcp-bridge-x" }],
              "x-guideants-tool-source": { "kind": "mcp", "transport": "streamable_http" },
              "paths": {
                "/tools/a": { "post": { "operationId": "a", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        ToolSourceValidator.ValidateDescriptor(spec, publishChecks: true)
            .Should().Contain(m => m.Code == "missing_mcp_server_url" && m.Severity == "error");
    }

    [TestMethod]
    public void EnsurePublishableOrThrow_Throws_for_blocking_validation_errors()
    {
        var act = () => ToolSourceValidator.EnsurePublishableOrThrow("{}", "Bad Tool");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Bad Tool*failed validation*");
    }

    [TestMethod]
    public void PreviewOperation_Returns_structured_metadata_for_web_spec()
    {
        var preview = ToolSourceValidator.PreviewOperation(OperationFragment, WebSpec);

        preview.SourceKind.Should().Be("web-api");
        preview.ActionType.Should().Be("WebApi");
        preview.ToolDefinition.Should().Contain("listItems");
        preview.ValidationMessages.Should().NotBeNull();
    }

    [TestMethod]
    public void PreviewOperation_Uses_parent_server_url_not_placeholder()
    {
        const string clientFragment = """
            {
              "path": "/start",
              "method": "post",
              "operation": {
                "operationId": "start",
                "summary": "Start",
                "responses": { "200": { "description": "ok" } }
              }
            }
            """;

        var preview = ToolSourceValidator.PreviewOperation(clientFragment, ClientSpec);

        preview.SourceKind.Should().Be("client-actions");
        preview.ActionType.Should().Be("ClientHandled");
    }

    [TestMethod]
    public void PreviewOperation_Reports_hidden_default_injected_parameters()
    {
        const string fragment = """
            {
              "path": "/items",
              "method": "post",
              "operation": {
                "operationId": "createItem",
                "summary": "Create",
                "requestBody": {
                  "required": true,
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": {
                          "name": { "type": "string" },
                          "mode": { "type": "string", "default": "auto" }
                        },
                        "required": ["name"]
                      }
                    }
                  }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
            """;

        var preview = ToolSourceValidator.PreviewOperation(fragment, WebSpec);

        preview.HiddenParameters.Should().Contain("mode");
        preview.ToolDefinition.Should().NotContain("mode");
    }

    [TestMethod]
    public void ClassifySourceKind_Matches_runtime_matrix()
    {
        ToolSourceValidator.ClassifySourceKind(WebSpec).Should().Be("web-api");
        ToolSourceValidator.ClassifySourceKind(ClientSpec).Should().Be("client-actions");
        ToolSourceValidator.ClassifySourceKind(SandboxSpec).Should().Be("sandbox-module");
        ToolSourceValidator.ClassifySourceKind(ToolSpec).Should().Be("local-function");
        ToolSourceValidator.ClassifySourceKind(McpClientBridgeSpec).Should().Be("mcp-connection");
    }

    [TestMethod]
    public void ResolveActionType_Maps_mcp_to_ClientHandled()
    {
        ToolSourceValidator.ResolveActionType("mcp-connection", McpClientBridgeSpec)
            .Should().Be("ClientHandled");
        ToolSourceValidator.ResolveActionType("sandbox-module", SandboxSpec)
            .Should().Be("SandboxHandled");
    }
}
