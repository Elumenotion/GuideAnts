using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class OpenApiHelperTests
{
    private const string ValidOpenApiJson = """
        {
          "openapi": "3.0.0",
          "servers": [{ "url": "https://api.example.com" }],
          "paths": {
            "/items": {
              "get": {
                "operationId": "listItems",
                "summary": "List items",
                "parameters": [
                  {
                    "name": "limit",
                    "in": "query",
                    "required": true,
                    "schema": { "type": "integer", "default": 10 }
                  }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    [TestMethod]
    public void ValidateAndParseOpenApiSpec_Accepts_valid_json_spec()
    {
        var result = OpenApiHelper.ValidateAndParseOpenApiSpec(ValidOpenApiJson);

        result.Status.Should().BeTrue();
        result.Spec.Should().NotBeNull();
    }

    [TestMethod]
    public void ValidateAndParseOpenApiSpec_Rejects_missing_servers()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "paths": {
                "/items": { "get": { "operationId": "listItems", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        var result = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);

        result.Status.Should().BeFalse();
        result.Message.Should().Contain("servers");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_Extracts_operation_metadata()
    {
        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(ValidOpenApiJson);
        validation.Status.Should().BeTrue();

        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);

        tools.Should().ContainSingle();
        var function = tools[0].Function!.AsObject!;
        function.Name.Should().Be("listItems");
        function.Description.Should().Be("List items");
        function.Parameters!.Properties.Should().ContainKey("limit");
    }

    [TestMethod]
    public void ValidateAndParseOpenApiSpec_Accepts_yaml_spec()
    {
        const string yaml = """
            openapi: 3.0.0
            servers:
              - url: https://api.example.com
            paths:
              /ping:
                get:
                  operationId: ping
                  responses:
                    '200':
                      description: ok
            """;

        var result = OpenApiHelper.ValidateAndParseOpenApiSpec(yaml);

        result.Status.Should().BeTrue();
        result.Spec.Should().NotBeNull();
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_Handles_request_body_object_properties()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/create": {
                  "post": {
                    "operationId": "createItem",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "required": ["name", "count"],
                            "properties": {
                              "name": { "type": "string" },
                              "count": { "type": "integer" }
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

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);

        tools.Should().ContainSingle();
        var parameters = tools[0].Function!.AsObject!.Parameters!;
        parameters.Properties.Should().ContainKey("name");
        parameters.Properties.Should().ContainKey("count");
    }

    [TestMethod]
    public void GetToolDefinitionsFromJson_Returns_empty_for_invalid_spec()
    {
        var tools = OpenApiHelper.GetToolDefinitionsFromJson("{ not valid openapi }");

        tools.Should().BeEmpty();
    }

    [TestMethod]
    public void ValidateAndParseOpenApiSpec_Rejects_empty_paths()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {}
            }
            """;

        var result = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);

        result.Status.Should().BeFalse();
        result.Message.Should().Contain("paths", "because empty paths are invalid");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_Handles_query_parameter_enums()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/status": {
                  "get": {
                    "operationId": "getStatus",
                    "parameters": [
                      {
                        "name": "level",
                        "in": "query",
                        "required": true,
                        "schema": { "type": "string", "enum": ["info", "warn", "error"] }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);

        tools.Should().ContainSingle();
        tools[0].Function!.AsObject!.Parameters!.Properties!["level"].Enum.Should().Contain("error");
    }

    [TestMethod]
    public void ValidateAndParseOpenApiSpec_Rejects_invalid_json()
    {
        var result = OpenApiHelper.ValidateAndParseOpenApiSpec("{ broken");

        result.Status.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_Handles_openapi_31_array_type()
    {
        const string spec = """
            {
              "openapi": "3.1.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "parameters": [
                      {
                        "name": "tag",
                        "in": "query",
                        "schema": { "type": ["string", "null"] }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        validation.Status.Should().BeTrue();

        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);
        tools.Should().ContainSingle();
        tools[0].Function!.AsObject!.Parameters!.Properties.Should().ContainKey("tag");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_Includes_operation_with_generated_parameters()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/orphan": {
                  "get": {
                    "operationId": "orphanOp",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);

        tools.Should().ContainSingle();
        tools[0].Function!.AsObject!.Name.Should().Be("orphanOp");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_HidesDefaultAndSingleValueEnumBodyProperties()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/create": {
                  "post": {
                    "operationId": "createItem",
                    "requestBody": {
                      "content": {
                        "application/json": {
                          "schema": {
                            "type": "object",
                            "required": ["mode", "tier", "name"],
                            "properties": {
                              "mode": { "type": "string", "default": "fast" },
                              "tier": { "type": "string", "enum": ["gold"] },
                              "name": { "type": "string" }
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

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);
        var parameters = tools[0].Function!.AsObject!.Parameters!;

        parameters.Properties.Should().ContainKey("name");
        parameters.Properties.Should().NotContainKey("mode");
        parameters.Properties.Should().NotContainKey("tier");
        parameters.Required.Should().ContainSingle().Which.Should().Be("name");
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_ConvertsEnumValuesToStrings()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/status": {
                  "get": {
                    "operationId": "getStatus",
                    "parameters": [
                      {
                        "name": "flag",
                        "in": "query",
                        "schema": { "type": "string", "enum": [1, true, false, "x", null] }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);
        var enumValues = tools[0].Function!.AsObject!.Parameters!.Properties!["flag"].Enum!;

        enumValues.Should().Contain("1");
        enumValues.Should().Contain("true");
        enumValues.Should().Contain("false");
        enumValues.Should().Contain("x");
        enumValues.Should().NotContainNulls();
    }

    [TestMethod]
    public void GetToolDefinitionsFromSchema_HandlesPrimitiveRequestBodyExample()
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
                          "schema": {
                            "type": "integer",
                            "example": 99
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

        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(spec);
        var tools = OpenApiHelper.GetToolDefinitionsFromSchema(validation.Spec!);
        var requestBody = tools[0].Function!.AsObject!.Parameters!.Properties!["requestBody"];

        requestBody.Type.Should().Be("integer");
        requestBody.Example.Should().Be("99");
    }
}
