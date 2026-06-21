using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Tests.BackgroundJobs;
using Moq;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuidesServiceDeepTests
{
    private const string OpenApiSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Example API", "version": "1.0.0" },
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

    // ----- NormalizeAndValidateModelParametersAsync branches -----

    [TestMethod]
    public async Task CreateGuideAsync_Throws_when_reasoningEffort_set_without_modelId()
    {
        await using var context = NewContext("reason-no-model");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = null, ReasoningEffort = "high" };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ModelId is required when reasoningEffort is specified*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_Throws_when_model_not_found()
    {
        await using var context = NewContext("model-missing");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "ghost", Temperature = 0.5f };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Model 'ghost' was not found*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_NoParameters_skips_model_lookup_even_for_missing_model()
    {
        await using var context = NewContext("model-noparams");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        // No temperature/topP/reasoning/sampling => NormalizedModelParameters.Empty short circuit
        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "ghost" };

        var created = await service.CreateGuideAsync(dto);

        created.ModelId.Should().Be("ghost");
    }

    [TestMethod]
    public async Task CreateGuideAsync_CatalogModel_accepts_valid_reasoningEffort()
    {
        await using var context = NewContext("catalog-reason-ok");
        context.Models.Add(new Model
        {
            ModelId = "gpt-x",
            DisplayName = "GPT X",
            Provider = "openai-chat",
            ReasoningChoicesJson = "[\"low\",\"medium\",\"high\"]"
        });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "gpt-x", ReasoningEffort = "high" };

        var created = await service.CreateGuideAsync(dto);

        var details = await service.GetGuideAsync(created.Id);
        details!.ReasoningEffort.Should().Be("high");
    }

    [TestMethod]
    public async Task CreateGuideAsync_CatalogModel_rejects_invalid_reasoningEffort()
    {
        await using var context = NewContext("catalog-reason-bad");
        context.Models.Add(new Model
        {
            ModelId = "gpt-x",
            DisplayName = "GPT X",
            Provider = "openai-chat",
            ReasoningChoicesJson = "[\"low\",\"high\"]"
        });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "gpt-x", ReasoningEffort = "ultra" };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Reasoning effort 'ultra' is invalid*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_CatalogModel_rejects_reasoningEffort_when_no_choices_defined()
    {
        await using var context = NewContext("catalog-reason-none");
        context.Models.Add(new Model
        {
            ModelId = "gpt-x",
            DisplayName = "GPT X",
            Provider = "openai-chat",
            ReasoningChoicesJson = null
        });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "gpt-x", ReasoningEffort = "high" };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not define reasoning choices*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_CatalogModel_rejects_invalid_reasoningChoices_json()
    {
        await using var context = NewContext("catalog-reason-badjson");
        context.Models.Add(new Model
        {
            ModelId = "gpt-x",
            DisplayName = "GPT X",
            Provider = "openai-chat",
            ReasoningChoicesJson = "{not-an-array"
        });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "gpt-x", ReasoningEffort = "high" };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid ReasoningChoicesJson*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_CatalogModel_drops_temperature_and_topP()
    {
        await using var context = NewContext("catalog-drop-temp");
        context.Models.Add(new Model
        {
            ModelId = "gpt-x",
            DisplayName = "GPT X",
            Provider = "openai-chat"
        });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "gpt-x", Temperature = 0.5f, TopP = 0.9 };

        var created = await service.CreateGuideAsync(dto);

        var details = await service.GetGuideAsync(created.Id);
        details!.Temperature.Should().BeNull();
        details.TopP.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateGuideAsync_LocalModel_accepts_in_range_sampling_parameters()
    {
        await using var context = NewContext("local-sampling-ok");
        context.Models.Add(new Model
        {
            ModelId = "local-x",
            DisplayName = "Local X",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"router","runtimeProfileId":"profile-a"}"""
        });
        await context.SaveChangesAsync();

        var resolver = CreateResolverWithTemperatureRange("profile-a", min: 0, max: 2);
        var service = GuidesServiceTestHelper.CreateGuidesService(context, resolver);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "local-x", Temperature = 0.7f };

        var created = await service.CreateGuideAsync(dto);

        var details = await service.GetGuideAsync(created.Id);
        details!.Temperature.Should().BeApproximately(0.7f, 0.0001f);
    }

    [TestMethod]
    public async Task CreateGuideAsync_LocalModel_rejects_out_of_range_temperature()
    {
        await using var context = NewContext("local-sampling-bad");
        context.Models.Add(new Model
        {
            ModelId = "local-x",
            DisplayName = "Local X",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"router","runtimeProfileId":"profile-a"}"""
        });
        await context.SaveChangesAsync();

        var resolver = CreateResolverWithTemperatureRange("profile-a", min: 0, max: 1);
        var service = GuidesServiceTestHelper.CreateGuidesService(context, resolver);

        var dto = MinimalCreateGuideDto("Guide") with { ModelId = "local-x", Temperature = 5f };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*out of range*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_LocalModel_rejects_unsupported_sampling_parameter()
    {
        await using var context = NewContext("local-sampling-unsupported");
        context.Models.Add(new Model
        {
            ModelId = "local-x",
            DisplayName = "Local X",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"router","runtimeProfileId":"profile-a"}"""
        });
        await context.SaveChangesAsync();

        var resolver = CreateResolverWithTemperatureRange("profile-a", min: 0, max: 2);
        var service = GuidesServiceTestHelper.CreateGuidesService(context, resolver);

        var dto = MinimalCreateGuideDto("Guide") with
        {
            ModelId = "local-x",
            SamplingParametersJson = "{\"mirostat\":3.0}"
        };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is not supported by model*");
    }

    // ----- CreateAssistantEntity validation branches -----

    [TestMethod]
    public async Task CreateGuideAsync_Throws_on_duplicate_custom_tool_names()
    {
        await using var context = NewContext("dup-tools");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with
        {
            CustomTools =
            [
                new CustomToolDto("DupApi", OpenApiSpec, "https://api.example.com", null),
                new CustomToolDto("DupApi", OpenApiSpec, "https://api.example.com", null)
            ]
        };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Duplicate OpenAPI schema names*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_Throws_when_auth_config_has_no_apiHost()
    {
        await using var context = NewContext("auth-no-host");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with
        {
            CustomTools =
            [
                new CustomToolDto(
                    "ApiTool",
                    OpenApiSpec,
                    ApiHost: null,
                    new OpenApiAuthConfigDto("apiKey", null, null, null, "secret", "X-Api-Key", "none"))
            ]
        };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*auth configuration but no ApiHost*");
    }

    [TestMethod]
    public async Task CreateGuideAsync_AutoEnables_SetContextOptions_tool_when_value_empty()
    {
        await using var context = NewContext("auto-context-tool");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateGuideDto("Guide") with
        {
            ContextOptions = [new ContextOptionDto("region", null)]
        };

        var created = await service.CreateGuideAsync(dto);

        var setContextToolId = new Guid("b0000000-0000-0000-0000-00000000000c");
        var guide = await context.Assistants
            .FindAsync(created.Id);
        await context.Entry(guide!).Collection(a => a.Tools).LoadAsync();
        guide!.Tools.Should().Contain(t => t.ToolId == setContextToolId);
    }

    [TestMethod]
    public async Task CreateGuideAsync_Skips_files_with_empty_content_or_path()
    {
        await using var context = NewContext("skip-empty-files");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        // Use a non-VectorStore folder kind so GetGuideAsync does not attempt to
        // load markdown shadows (which would require a scoped ApplicationDbContext).
        var dto = MinimalCreateGuideDto("Guide") with
        {
            Files =
            [
                new FileUploadDto("CodeInterpreter", null, "empty.txt", [], "text/plain"),
                new FileUploadDto("CodeInterpreter", null, "", [1, 2, 3], "text/plain"),
                new FileUploadDto("CodeInterpreter", null, "good.txt", [1, 2, 3], "text/plain")
            ]
        };

        var created = await service.CreateGuideAsync(dto);

        var details = await service.GetGuideAsync(created.Id);
        details!.Files.Should().ContainSingle(f => f.RelativePath == "good.txt");
    }

    // ----- GetGuideAsync auth parse error branch -----

    [TestMethod]
    public async Task GetGuideAsync_Tolerates_invalid_AuthConfigJson()
    {
        await using var context = NewContext("bad-authconfig");
        var guide = new Assistant
        {
            Name = "Guide",
            Kind = AssistantKind.Guide,
            Created = DateTime.UtcNow,
            AuthConfigJson = "{ not valid json"
        };
        context.Assistants.Add(guide);
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var details = await service.GetGuideAsync(guide.Id);

        details.Should().NotBeNull();
        details!.AuthProviders.Should().BeNull();
    }

    // ----- Operations -----

    [TestMethod]
    public async Task GetOperationAsync_Returns_null_when_missing()
    {
        await using var context = NewContext("op-missing");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        (await service.GetOperationAsync(Guid.NewGuid())).Should().BeNull();
    }

    [TestMethod]
    public async Task GetOperationAsync_Returns_dto_when_found()
    {
        await using var context = NewContext("op-found");
        var operationId = await SeedOperationAsync(context);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var op = await service.GetOperationAsync(operationId);

        op.Should().NotBeNull();
        op!.OperationId.Should().Be("listItems");
    }

    [TestMethod]
    public async Task UpdateOperationAsync_Throws_for_invalid_fragment()
    {
        await using var context = NewContext("op-update-bad");
        var operationId = await SeedOperationAsync(context);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.UpdateOperationAsync(operationId, new UpdateOperationDto("{ invalid"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to update operation*");
    }

    [TestMethod]
    public async Task UpdateOperationAsync_Throws_when_operation_missing()
    {
        await using var context = NewContext("op-update-missing");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.UpdateOperationAsync(Guid.NewGuid(), new UpdateOperationDto("{}"));

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ----- Avatars / duplicate -----

    [TestMethod]
    public async Task GetGuideAvatarBytesAsync_Returns_bytes_and_null()
    {
        await using var context = NewContext("guide-avatar");
        var guide = new Assistant
        {
            Name = "Guide",
            Kind = AssistantKind.Guide,
            Created = DateTime.UtcNow,
            AvatarImageBytes = [9, 8, 7]
        };
        context.Assistants.Add(guide);
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        (await service.GetGuideAvatarBytesAsync(guide.Id)).Should().Equal((byte)9, (byte)8, (byte)7);
        (await service.GetGuideAvatarBytesAsync(Guid.NewGuid())).Should().BeNull();
    }

    [TestMethod]
    public async Task GetAssistantAvatarBytesAsync_Returns_bytes_and_null()
    {
        await using var context = NewContext("assistant-avatar");
        var assistant = new Assistant
        {
            Name = "A",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow,
            AvatarImageBytes = [1, 2]
        };
        context.Assistants.Add(assistant);
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        (await service.GetAssistantAvatarBytesAsync(assistant.Id)).Should().Equal((byte)1, (byte)2);
        (await service.GetAssistantAvatarBytesAsync(Guid.NewGuid())).Should().BeNull();
    }

    [TestMethod]
    public async Task DuplicateAssistantAsync_Throws_not_implemented()
    {
        await using var context = NewContext("assistant-dup");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.DuplicateAssistantAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    // NOTE: UpdateAssistantEntityAsync collection replacement (tools, context options,
    // conversation starters, crew members, files) uses ExecuteDeleteAsync, which the
    // EF Core InMemory provider does not support. Those relational-only paths are
    // intentionally not covered here (see task report).

    // ----- helpers -----

    private static ApplicationDbContext NewContext(string name) =>
        new(BackgroundJobTestHelpers.CreateInMemoryOptions($"{name}-{Guid.NewGuid():N}"));

    private static IRuntimeProfileResolver CreateResolverWithTemperatureRange(string profileId, double min, double max)
    {
        var resolver = new Mock<IRuntimeProfileResolver>();
        resolver.Setup(r => r.ResolveAsync(profileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuntimeProfileData(
                profileId,
                CombineSystemAndDeveloperMessages: false,
                ThoughtBlockPattern: null,
                SamplingParameters: new Dictionary<string, SamplingParameterDefinition>
                {
                    ["temperature"] = new("temperature", "Temperature", "", min, max, 0.1, 0.7, 0, true)
                },
                ThinkingControl: new ThinkingControl("None", new Dictionary<string, IReadOnlyList<ThinkingAction>>())));
        return resolver.Object;
    }

    private static async Task<Guid> SeedOperationAsync(ApplicationDbContext context)
    {
        var assistant = new Assistant { Name = "A", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow };
        context.Assistants.Add(assistant);
        var schema = new AssistantOpenApiSchema
        {
            Id = Guid.NewGuid(),
            AssistantId = assistant.Id,
            Name = "Api",
            ApiHost = "https://api.example.com",
            SpecificationJson = OpenApiSpec
        };
        context.AssistantOpenApiSchemas.Add(schema);
        var operation = new AssistantOpenApiOperation
        {
            Id = Guid.NewGuid(),
            SchemaId = schema.Id,
            OperationId = "listItems",
            Method = "GET",
            Path = "/items",
            Summary = "List items",
            ToolDefinitionJson = "{}",
            SchemaFragmentJson = "{}",
            Created = DateTime.UtcNow
        };
        context.AssistantOpenApiOperations.Add(operation);
        await context.SaveChangesAsync();
        return operation.Id;
    }

    private static CreateGuideDto MinimalCreateGuideDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            HomePageMarkdown: "# Home",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            AuthProviders: null,
            Files: null,
            ConversationStarters: null,
            CrewMemberIds: null);

    private static UpdateGuideDto MinimalUpdateGuideDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            HomePageMarkdown: "# Home",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            AuthProviders: null,
            FileIdsToKeep: null,
            FilesToAdd: null,
            ConversationStarters: null,
            CrewMemberIds: null);

    private static CreateAssistantDto MinimalCreateAssistantDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            Files: null,
            ConversationStarters: null);

    private static UpdateAssistantDto MinimalUpdateAssistantDto(string name) =>
        new(
            Name: name,
            Description: "desc",
            Instructions: "helpful",
            ModelId: null,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions: null,
            FileIdsToKeep: null,
            FilesToAdd: null,
            ConversationStarters: null);
}
