using System.Reflection;
using System.Text.Json;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using AntRunner.ToolCalling.Functions;
using GuideAntsApi.DataModel.Models;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

/// <summary>
/// Coverage for the pure materialization/parsing logic in <see cref="DatabaseStorage"/>.
/// The DB-touching public methods create a SQL Server context internally, so the manifest
/// builders are exercised directly via their (private static) entry points using reflection,
/// plus the public connection-string and reasoning-effort guard branches.
/// </summary>
[TestClass]
public sealed class DatabaseStorageDeepTests
{
    private static readonly Type StorageType = typeof(DatabaseStorage);

    private static T Invoke<T>(string method, params object?[] args)
    {
        var mi = StorageType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                 ?? throw new InvalidOperationException($"Method {method} not found.");
        return (T)mi.Invoke(null, args)!;
    }

    private static AssistantStorageMetadata Materialize(Assistant assistant) =>
        Invoke<AssistantStorageMetadata>("MaterializeAssistant", assistant);

    [TestMethod]
    public void MaterializeAssistant_FullGuide_BuildsManifestSchemasCrewAndDomainAuth()
    {
        var crewMember = new Assistant { Name = "Researcher" };
        var assistant = new Assistant
        {
            Id = Guid.NewGuid(),
            Name = "Office Guide",
            Description = "A guide",
            ModelId = "gpt-4o",
            InvocationEvaluator = "evaluator",
            Instructions = "Do the thing",
            Kind = AssistantKind.Guide,
            IsGlobal = true,
            Updated = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MetadataJson = """{"family":"office"}""",
            SamplingParametersJson = """{"temperature":0.5}""",
            Tools = [new AssistantTool { Tool = new Tool { ToolType = "web_search" } }],
            Files =
            [
                new AssistantFile { FolderKind = "CodeInterpreter", RelativePath = "code/a.py" },
                new AssistantFile
                {
                    FolderKind = "VectorStore",
                    VectorStoreName = "vs1",
                    RelativePath = "docs/readme.md",
                    ContentBytes = [1, 2, 3]
                }
            ],
            ContextOptions = [new AssistantContextOption { Key = "tone", Value = "formal" }],
            CrewMembers = [new GuideMember { Assistant = crewMember, DisplayOrder = 1 }],
            OpenApiSchemas =
            [
                new AssistantOpenApiSchema
                {
                    Name = "search",
                    ApiHost = "https://api.search.test/v1",
                    SpecificationJson = """{"openapi":"3.0.0"}""",
                    AuthProvider = new AssistantAuthProvider
                    {
                        ProviderId = "api.search.test",
                        AuthType = "service_http",
                        HeaderName = "x-api-key",
                        ValueTemplate = "SEARCH_KEY"
                    }
                }
            ]
        };

        var metadata = Materialize(assistant);

        metadata.Instructions.Should().Be("Do the thing");
        metadata.Updated.Should().Be(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        metadata.SamplingParametersJson.Should().Be("""{"temperature":0.5}""");

        using var manifest = JsonDocument.Parse(metadata.ManifestJson);
        manifest.RootElement.GetProperty("name").GetString().Should().Be("Office Guide");
        var toolTypes = manifest.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("type").GetString()).ToList();
        toolTypes.Should().Contain("web_search");
        toolTypes.Should().Contain("code_interpreter");
        toolTypes.Should().Contain("file_search");

        metadata.AdditionalMetadata.Should().ContainKey("__crew_names__");
        metadata.AdditionalMetadata!["__crew_names__"].Should().Be("Researcher");

        metadata.ContextOptionsJson.Should().Contain("tone");
        metadata.OpenApiSchemas.Should().NotBeNull();
        metadata.OpenApiSchemas!.Keys.Should().Contain(k => k.Contains("api.search.test"));
        metadata.VectorStoreFiles.Should().NotBeNull();
        metadata.VectorStoreFiles!.Should().ContainKey("vs1/docs/readme.md");

        metadata.DomainAuth.Should().NotBeNull();
        metadata.DomainAuth!.HostAuthorizationConfigurations.Should().ContainKey("api.search.test");
        var cfg = metadata.DomainAuth.HostAuthorizationConfigurations["api.search.test"];
        cfg.AuthType.Should().Be(AuthType.service_http);
        // Global assistants carry the env-var template rather than a literal value.
        cfg.HeaderValueEnvironmentVariable.Should().Be("SEARCH_KEY");
        cfg.HeaderValueLiteral.Should().BeNull();
    }

    [TestMethod]
    public void MaterializeAssistant_NonGlobalWithoutExtras_OmitsOptionalSections()
    {
        var assistant = new Assistant
        {
            Name = "Plain",
            Kind = AssistantKind.Assistant,
            IsGlobal = false,
            OpenApiSchemas =
            [
                new AssistantOpenApiSchema
                {
                    Name = "svc",
                    ApiHost = "api.plain.test",
                    SpecificationJson = """{"openapi":"3.0.0"}""",
                    AuthProvider = new AssistantAuthProvider
                    {
                        ProviderId = "api.plain.test",
                        AuthType = "service_query",
                        HeaderName = "key",
                        ValueTemplate = "literal-secret"
                    }
                }
            ]
        };

        var metadata = Materialize(assistant);

        metadata.AdditionalMetadata.Should().BeNull();
        metadata.ContextOptionsJson.Should().BeNull();
        metadata.VectorStoreFiles.Should().BeNull();
        // Non-global assistants store the literal value rather than an env-var reference.
        var cfg = metadata.DomainAuth!.HostAuthorizationConfigurations["api.plain.test"];
        cfg.AuthType.Should().Be(AuthType.service_query);
        cfg.HeaderValueLiteral.Should().Be("literal-secret");
        cfg.HeaderValueEnvironmentVariable.Should().BeNull();
    }

    [TestMethod]
    public void MaterializeAssistant_NoSchemas_ReturnsNullDomainAuthAndSchemas()
    {
        var assistant = new Assistant { Name = "NoApi", Kind = AssistantKind.Assistant };

        var metadata = Materialize(assistant);

        metadata.DomainAuth.Should().BeNull();
        metadata.OpenApiSchemas.Should().BeNull();
    }

    [TestMethod]
    public void BuildToolResources_PrefersExplicitToolResourcesJson()
    {
        var assistant = new Assistant
        {
            Name = "x",
            ToolResourcesJson = """{"file_search":{"vector_store_ids":["abc"]}}"""
        };

        var result = Invoke<object?>("BuildToolResources", assistant);

        result.Should().NotBeNull();
        var json = JsonSerializer.Serialize(result);
        json.Should().Contain("abc");
    }

    [TestMethod]
    public void BuildToolResources_InvalidJsonWithNoVectorFiles_ReturnsNull()
    {
        var assistant = new Assistant { Name = "x", ToolResourcesJson = "{ not valid json" };

        var result = Invoke<object?>("BuildToolResources", assistant);

        result.Should().BeNull();
    }

    [TestMethod]
    public void BuildToolResources_DerivesFromVectorStoreFiles()
    {
        var assistant = new Assistant
        {
            Name = "x",
            Files =
            [
                new AssistantFile { FolderKind = "VectorStore", VectorStoreName = "store-1", RelativePath = "a.txt" }
            ]
        };

        var result = Invoke<object?>("BuildToolResources", assistant);

        JsonSerializer.Serialize(result).Should().Contain("store-1");
    }

    [TestMethod]
    public void ParseMetadataJson_InvalidJson_ReturnsNull()
    {
        Invoke<Dictionary<string, string>?>("ParseMetadataJson", "{not json").Should().BeNull();
        Invoke<Dictionary<string, string>?>("ParseMetadataJson", (string?)null).Should().BeNull();
    }

    [TestMethod]
    public void ParseReasoningChoicesJson_HandlesValidInvalidAndEmpty()
    {
        Invoke<List<string>>("ParseReasoningChoicesJson", """["low"," high ",""]""")
            .Should().Equal("low", "high");
        Invoke<List<string>>("ParseReasoningChoicesJson", "not-json").Should().BeEmpty();
        Invoke<List<string>>("ParseReasoningChoicesJson", "   ").Should().BeEmpty();
    }

    [TestMethod]
    public void NormalizeApiAuthority_HandlesAbsoluteBareHostAndWhitespace()
    {
        Invoke<string>("NormalizeApiAuthority", "https://graph.microsoft.com/v1.0")
            .Should().Be("graph.microsoft.com");
        Invoke<string>("NormalizeApiAuthority", "api.bare-host.test")
            .Should().Be("api.bare-host.test");
        Invoke<string>("NormalizeApiAuthority", "   ").Should().Be("   ");
    }

    [TestMethod]
    public async Task ResolveModelReasoningEffortAsync_NullArgs_ReturnNullWithoutDatabase()
    {
        (await DatabaseStorage.ResolveModelReasoningEffortAsync(null, "high")).Should().BeNull();
        (await DatabaseStorage.ResolveModelReasoningEffortAsync("gpt-4o", "   ")).Should().BeNull();
    }

    [TestMethod]
    public async Task GetAssistantMetadata_NoConnectionString_ReturnsNull()
    {
        const string key = "ConnectionStrings:DefaultConnection";
        var original = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);

            (await DatabaseStorage.GetAssistantMetadata("anything")).Should().BeNull();
            (await DatabaseStorage.GetAssistant("anything")).Should().BeNull();
            (await DatabaseStorage.GetAssistantAvatarAsync("anything")).Should().BeNull();
            (await DatabaseStorage.GetAssistantConversationStartersAsync("anything")).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }
}
