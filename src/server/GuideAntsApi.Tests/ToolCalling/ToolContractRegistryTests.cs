using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class ToolContractRegistryTests
{
    private static readonly string RunPythonMethodName = $"{typeof(DockerScriptWrappers).FullName}.RunPython";

    [TestMethod]
    public void GetContract_ForAnnotatedMethod_ReturnsToolMetadataAndParameterAttributes()
    {
        ToolContractRegistry.RefreshContracts();

        var contract = ToolContractRegistry.GetContract(RunPythonMethodName);

        contract.RequiresNotebookContext.Should().BeTrue();
        contract.OAuthPolicy.Should().Be(OAuthPolicy.None);
        contract.ToolMetadata.Should().NotBeNull();
        contract.ToolMetadata!.OperationId.Should().Be("run_python");
        contract.ParameterMetadata.Should().ContainKey("script");
        contract.ParameterMetadata.Should().ContainKey("containerName");
        contract.ParameterMetadata.Should().ContainKey("scriptType");
        contract.ParameterMetadata["scriptType"].Hidden.Should().BeTrue();
        contract.ParameterMetadata.Should().ContainKey("context");
        contract.ParameterMetadata["context"].Hidden.Should().BeTrue();
    }

    [TestMethod]
    public void GetContract_ForUnknownMethod_ReturnsDefaultContract()
    {
        var contract = ToolContractRegistry.GetContract("Unknown.Namespace.Tool");

        contract.RequiresNotebookContext.Should().BeFalse();
        contract.OAuthPolicy.Should().Be(OAuthPolicy.None);
        contract.ToolMetadata.Should().BeNull();
        contract.ParameterMetadata.Should().BeEmpty();
    }

    [TestMethod]
    public void GenerateOpenApiSchema_ForRunPython_ExcludesHiddenParametersFromRequestSchema()
    {
        ToolContractRegistry.RefreshContracts();

        var schemaJson = ToolContractRegistry.GenerateOpenApiSchema(RunPythonMethodName);
        using var doc = JsonDocument.Parse(schemaJson);
        var post = doc.RootElement
            .GetProperty("paths")
            .GetProperty(RunPythonMethodName)
            .GetProperty("post");

        post.GetProperty("operationId").GetString().Should().Be("run_python");

        var requestSchema = post
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application_json")
            .GetProperty("schema");

        var properties = requestSchema.GetProperty("properties");
        properties.TryGetProperty("script", out _).Should().BeTrue();
        properties.TryGetProperty("containerName", out _).Should().BeTrue();
        properties.TryGetProperty("scriptType", out _).Should().BeFalse();
        properties.TryGetProperty("context", out _).Should().BeFalse();

        var required = requestSchema.GetProperty("required").EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        required.Should().BeEmpty();
    }
}
