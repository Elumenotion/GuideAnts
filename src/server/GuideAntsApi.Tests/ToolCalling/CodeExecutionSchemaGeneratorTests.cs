using AntRunner.ToolCalling.Functions;
using FluentAssertions;
using System.Text.Json;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class CodeExecutionSchemaGeneratorTests
{
    [TestMethod]
    public void GetSchema_Returns_valid_openapi_json_with_code_execution_paths()
    {
        var json = CodeExecutionSchemaGenerator.GetSchema();

        json.Should().NotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("openapi").GetString().Should().Be("3.0.1");
        root.GetProperty("info").GetProperty("title").GetString().Should().Be("Code Execution Tools");
        root.TryGetProperty("paths", out var paths).Should().BeTrue();
        paths.EnumerateObject().Should().NotBeEmpty();
    }

    [TestMethod]
    public void GetSchema_Includes_python_bash_and_diagram_operations()
    {
        var json = CodeExecutionSchemaGenerator.GetSchema();
        using var doc = JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("paths").EnumerateObject().First().Value;

        path.TryGetProperty("python", out _).Should().BeTrue();
        path.TryGetProperty("bash", out _).Should().BeTrue();
        path.TryGetProperty("plantumlScript", out _).Should().BeTrue();

        path.GetProperty("python").GetProperty("operationId").GetString().Should().Be("runPython");
        path.GetProperty("bash").GetProperty("operationId").GetString().Should().Be("runBash");
        path.GetProperty("plantumlScript").GetProperty("operationId").GetString().Should().Be("makeDiagram");
    }
}
