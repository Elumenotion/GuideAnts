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
        tools[0].Function!.AsObject!.Name.Should().Be("listItems");
        tools[0].Function.AsObject.Description.Should().Be("List items");
        tools[0].Function.AsObject.Parameters!.Properties.Should().ContainKey("limit");
    }
}
