using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class OpenApiHelperDeepTests
{
    private static List<AntRunner.ToolCalling.AssistantDefinitions.ToolDefinition> ToolsFor(string spec)
    {
        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        validation.Status.Should().BeTrue();
        return OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_GeneratesOperationId_WhenMissing()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/widgets": {
                  "get": { "responses": { "200": { "description": "ok" } } }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        tools.Should().ContainSingle();
        tools[0].Function!.AsObject!.Name.Should().Be("get__widgets");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_FallsBackToDescription_WhenNoSummary()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/widgets": {
                  "get": {
                    "operationId": "listWidgets",
                    "description": "A long description used as fallback",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        tools[0].Function!.AsObject!.Description.Should().Be("A long description used as fallback");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_UsesParameterLevelDescription_WhenSchemaHasNone()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/search": {
                  "get": {
                    "operationId": "search",
                    "parameters": [
                      {
                        "name": "q",
                        "in": "query",
                        "description": "param-level description",
                        "schema": { "type": "string" }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        tools[0].Function!.AsObject!.Parameters!.Properties!["q"].Description.Should().Be("param-level description");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_PreservesNonStringDefaultAsRawText()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/list": {
                  "get": {
                    "operationId": "list",
                    "parameters": [
                      {
                        "name": "limit",
                        "in": "query",
                        "schema": { "type": "integer", "default": 25 }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        tools[0].Function!.AsObject!.Parameters!.Properties!["limit"].Default.Should().Be("25");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_ArrayRequestBody_MapsItemPropertiesAndRequired()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/bulk": {
                  "post": {
                    "operationId": "bulkCreate",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "array",
                            "items": {
                              "type": "object",
                              "required": ["sku"],
                              "properties": {
                                "sku": { "type": "string" },
                                "qty": { "type": "integer" }
                              }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        var body = tools[0].Function!.AsObject!.Parameters!.Properties!["requestBody"];
        body.Type.Should().Be("array");
        body.Items.Should().NotBeNull();
        body.Items!.Properties.Should().ContainKey("sku");
        body.Items.Properties.Should().ContainKey("qty");
        body.Items.Required.Should().ContainSingle().Which.Should().Be("sku");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_ObjectProperty_ThatIsArray_PreservesItemsSchema()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/create": {
                  "post": {
                    "operationId": "createOrder",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "properties": {
                              "lines": {
                                "type": "array",
                                "items": {
                                  "type": "object",
                                  "required": ["productId"],
                                  "properties": {
                                    "productId": { "type": "string" },
                                    "count": { "type": "integer" }
                                  }
                                }
                              }
                            }
                          }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        var lines = tools[0].Function!.AsObject!.Parameters!.Properties!["lines"];
        lines.Type.Should().Be("array");
        lines.Items.Should().NotBeNull();
        lines.Items!.Properties.Should().ContainKey("productId");
        lines.Items.Properties.Should().ContainKey("count");
        lines.Items.Required.Should().Contain("productId");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_CapturesContentTypeAndResponseSchema()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/echo": {
                  "post": {
                    "operationId": "echo",
                    "requestBody": {
                      "content": {
                        "text/plain": { "schema": { "type": "string" } }
                      }
                    },
                    "responses": {
                      "200": {
                        "content": {
                          "application/json": {
                            "schema": { "type": "object", "properties": { "ok": { "type": "boolean" } } }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var fn = ToolsFor(spec)[0].Function!.AsObject!;

        fn.ContentType.Should().Be("text/plain");
        fn.ResponseSchemas.Should().ContainKey("200");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_PrimitiveRequestBody_WithNullExample_LeavesExampleNull()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/score": {
                  "post": {
                    "operationId": "submitScore",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": { "type": "string", "example": null }
                        }
                      }
                    },
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var body = ToolsFor(spec)[0].Function!.AsObject!.Parameters!.Properties!["requestBody"];

        body.Type.Should().Be("string");
        body.Example.Should().BeNull();
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_MarksRequiredQueryParameters()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "parameters": [
                      { "name": "page", "in": "query", "required": true, "schema": { "type": "integer" } },
                      { "name": "filter", "in": "query", "schema": { "type": "string" } }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var parameters = ToolsFor(spec)[0].Function!.AsObject!.Parameters!;

        parameters.Required.Should().Contain("page");
        parameters.Required.Should().NotContain("filter");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_MultipleOperationsAcrossPaths_AreAllCaptured()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/a": { "get": { "operationId": "getA", "responses": { "200": { "description": "ok" } } } },
                "/b": {
                  "get": { "operationId": "getB", "responses": { "200": { "description": "ok" } } },
                  "post": { "operationId": "postB", "responses": { "200": { "description": "ok" } } }
                }
              }
            }
            """;

        var tools = ToolsFor(spec);

        tools.Select(t => t.Function!.AsObject!.Name).Should().BeEquivalentTo("getA", "getB", "postB");
    }

    [TestMethod]
    public void GetToolDefinitionsFromJson_ValidSpec_ReturnsDefinitions()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/ping": { "get": { "operationId": "ping", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        var tools = OpenApiHelper.GetToolDefinitionsFromJson(spec);

        tools.Should().ContainSingle();
        tools[0].Function!.AsObject!.Name.Should().Be("ping");
    }

    [TestMethod]
    public void ValidateAndParseOpenApiSpec_EmptyServersArray_IsRejected()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [],
              "paths": { "/x": { "get": { "operationId": "x", "responses": { "200": { "description": "ok" } } } } }
            }
            """;

        var result = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);

        result.Status.Should().BeFalse();
        result.Message.Should().Contain("servers");
    }
}
