using AntRunner.ToolCalling;
using FluentAssertions;
using AntRunner.Chat;

namespace GuideAntsApi.Tests.Services;

[TestClass]
public sealed class AssistantToolWrappersTests
{
    [TestMethod]
    public async Task ReadWeb_WhenUrlIsInvalid_ReturnsHelpfulValidationError()
    {
        var result = await AssistantToolWrappers.ReadWeb(
            "not-a-url",
            "Summarize the notable events on this page.");

        result.StandardOutput.Should().Contain("ERROR: Invalid URL for ReadWeb");
        result.StandardOutput.Should().Contain("https://example.com/article");
    }

    [TestMethod]
    public async Task ReadWeb_WhenInstructionsAreMissing_ReturnsHelpfulValidationError()
    {
        var result = await AssistantToolWrappers.ReadWeb("https://example.com/article", "   ");

        result.StandardOutput.Should().Contain("ERROR: Missing instructions for ReadWeb");
    }

    [TestMethod]
    public void ReadWeb_IsRegisteredWithUrlAndInstructionsParameters()
    {
        ToolContractRegistry.RefreshContracts();

        var tools = ToolContractRegistry.GetAllToolOperations();
        tools.Should().ContainKey("ReadWeb");
        tools["ReadWeb"].Should().Be("AntRunner.Chat.AssistantToolWrappers.ReadWeb");

        var contract = ToolContractRegistry.GetContract(tools["ReadWeb"]);
        contract.RequiresNotebookContext.Should().BeTrue();
        contract.ParameterMetadata.Should().ContainKey("url");
        contract.ParameterMetadata.Should().ContainKey("instructions");
        contract.ParameterMetadata["context"].Hidden.Should().BeTrue();

        var schema = ToolContractRegistry.GenerateOpenApiSchema(tools["ReadWeb"]);
        schema.Should().Contain("\"operationId\": \"ReadWeb\"");
        schema.Should().Contain("\"url\"");
        schema.Should().Contain("\"instructions\"");
        schema.Should().NotContain("\"context\"");
        schema.Should().Contain("\"required\"");
        schema.Should().Contain("\"url\"");
        schema.Should().Contain("\"instructions\"");
        using var doc = System.Text.Json.JsonDocument.Parse(schema);
        var required = doc.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .First()
            .Value
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application_json")
            .GetProperty("schema")
            .GetProperty("required");
        required.EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(["url", "instructions"]);
    }
}
