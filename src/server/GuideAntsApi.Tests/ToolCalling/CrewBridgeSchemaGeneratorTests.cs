using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class CrewBridgeSchemaGeneratorTests
{
    private const string PathKey = "AntRunner.Chat.Agent.Invoke";

    [TestMethod]
    public void GetSchema_NullCrew_Throws()
    {
        Action act = () => CrewBridgeSchemaGenerator.GetSchema(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void GetSchema_EmptyCrew_Throws()
    {
        Action act = () => CrewBridgeSchemaGenerator.GetSchema(new List<AssistantDefinition>());

        act.Should().Throw<ArgumentException>().WithMessage("Crew list is empty*");
    }

    [TestMethod]
    public void GetSchema_OnlyBlankNames_Throws()
    {
        var crew = new List<AssistantDefinition>
        {
            new() { Name = "   " },
            new() { Name = null }
        };

        Action act = () => CrewBridgeSchemaGenerator.GetSchema(crew);

        act.Should().Throw<ArgumentException>().WithMessage("Crew list is empty*");
    }

    [TestMethod]
    public void GetSchema_SingleAssistant_ProducesValidOpenApiDocument()
    {
        var crew = new List<AssistantDefinition>
        {
            new() { Name = "Researcher", Description = "Looks things up" }
        };

        var json = CrewBridgeSchemaGenerator.GetSchema(crew);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("openapi").GetString().Should().Be("3.0.0");
        root.GetProperty("info").GetProperty("title").GetString().Should().Be("Crew Bridge Tools");
        root.GetProperty("info").GetProperty("version").GetString().Should().Be("v1");
        root.GetProperty("servers")[0].GetProperty("url").GetString().Should().Be("tool://localhost");

        var pathObj = root.GetProperty("paths").GetProperty(PathKey);
        var op = pathObj.GetProperty("Researcher");
        op.GetProperty("operationId").GetString().Should().Be("Researcher");
        op.GetProperty("summary").GetString().Should().Be("Looks things up");

        var schema = op.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        var props = schema.GetProperty("properties");
        props.GetProperty("assistantName").GetProperty("type").GetString().Should().Be("string");
        props.GetProperty("assistantName").GetProperty("default").GetString().Should().Be("Researcher");
        props.GetProperty("instructions").GetProperty("type").GetString().Should().Be("string");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        required.Should().BeEquivalentTo("assistantName", "instructions");
    }

    [TestMethod]
    public void GetSchema_MissingDescription_UsesDefaultSummary()
    {
        var crew = new List<AssistantDefinition>
        {
            new() { Name = "Writer" }
        };

        var json = CrewBridgeSchemaGenerator.GetSchema(crew);

        using var doc = JsonDocument.Parse(json);
        var op = doc.RootElement.GetProperty("paths").GetProperty(PathKey).GetProperty("Writer");
        op.GetProperty("summary").GetString().Should().Be("Invoke assistant 'Writer' through Agent.Invoke");
    }

    [TestMethod]
    public void GetSchema_DuplicateNamesCaseInsensitive_AreDeduplicated()
    {
        var crew = new List<AssistantDefinition>
        {
            new() { Name = "Helper", Description = "first" },
            new() { Name = "HELPER", Description = "second" }
        };

        var json = CrewBridgeSchemaGenerator.GetSchema(crew);

        using var doc = JsonDocument.Parse(json);
        var pathObj = doc.RootElement.GetProperty("paths").GetProperty(PathKey);
        pathObj.EnumerateObject().Should().HaveCount(1);
        pathObj.GetProperty("Helper").GetProperty("summary").GetString().Should().Be("first");
    }

    [TestMethod]
    public void GetSchema_SpecialCharactersInName_SanitizesOperationIdAndDeduplicates()
    {
        var crew = new List<AssistantDefinition>
        {
            new() { Name = "a b" },
            new() { Name = "a/b" }
        };

        var json = CrewBridgeSchemaGenerator.GetSchema(crew);

        using var doc = JsonDocument.Parse(json);
        var pathObj = doc.RootElement.GetProperty("paths").GetProperty(PathKey);
        var operationIds = pathObj.EnumerateObject()
            .Select(p => p.Value.GetProperty("operationId").GetString())
            .ToArray();

        operationIds.Should().BeEquivalentTo(new[] { "a_b", "a_b_2" });
        foreach (var id in operationIds)
        {
            id.Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$");
        }
    }

    [TestMethod]
    public void GetSchema_AssistantNameDefaultMatchesOriginalNameNotSanitizedId()
    {
        var crew = new List<AssistantDefinition>
        {
            new() { Name = "Data Analyst" }
        };

        var json = CrewBridgeSchemaGenerator.GetSchema(crew);

        using var doc = JsonDocument.Parse(json);
        var op = doc.RootElement.GetProperty("paths").GetProperty(PathKey).GetProperty("Data_Analyst");
        op.GetProperty("operationId").GetString().Should().Be("Data_Analyst");
        op.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("assistantName")
            .GetProperty("default")
            .GetString()
            .Should().Be("Data Analyst");
    }
}
