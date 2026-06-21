using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Tests.BackgroundJobs;
using Moq;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuidesServiceDeepTests2
{
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

    // ----- Listing -----

    [TestMethod]
    public async Task GetGuidesAsync_ReturnsOnlyGuides_OrderedByCreated()
    {
        await using var context = NewContext("list-guides");
        context.Assistants.Add(new Assistant { Name = "Z Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow.AddMinutes(-1) });
        context.Assistants.Add(new Assistant { Name = "A Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow });
        context.Assistants.Add(new Assistant { Name = "An Assistant", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var guides = (await service.GetGuidesAsync()).ToList();

        guides.Should().HaveCount(2);
        guides[0].Name.Should().Be("Z Guide");
        guides.Should().OnlyContain(g => g.Name.EndsWith("Guide"));
    }

    [TestMethod]
    public async Task GetAssistantsAsync_ReturnsOnlyAssistants_OrderedByName_WithGuideMemberships()
    {
        await using var context = NewContext("list-assistants");
        var guide = new Assistant { Name = "Parent Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow };
        var assistant = new Assistant { Name = "Beta", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow };
        var assistant2 = new Assistant { Name = "Alpha", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow };
        context.Assistants.AddRange(guide, assistant, assistant2);
        await context.SaveChangesAsync();
        context.GuideMembers.Add(new GuideMember { GuideId = guide.Id, AssistantId = assistant.Id });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var assistants = (await service.GetAssistantsAsync()).ToList();

        assistants.Should().HaveCount(2);
        assistants[0].Name.Should().Be("Alpha");
        assistants.Single(a => a.Name == "Beta").CrewNames.Should().Contain("Parent Guide");
    }

    // ----- GetAssistantAsync rich read -----

    [TestMethod]
    public async Task GetAssistantAsync_ReturnsNull_WhenMissingOrWrongKind()
    {
        await using var context = NewContext("assistant-null");
        var guide = new Assistant { Name = "Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow };
        context.Assistants.Add(guide);
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        (await service.GetAssistantAsync(Guid.NewGuid())).Should().BeNull();
        (await service.GetAssistantAsync(guide.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task GetAssistantAsync_MapsContextOptionsConversationStartersAndFiles()
    {
        await using var context = NewContext("assistant-rich");
        var assistant = new Assistant
        {
            Name = "Rich",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow,
            Instructions = "be helpful"
        };
        context.Assistants.Add(assistant);
        await context.SaveChangesAsync();

        context.AssistantContextOptions.Add(new AssistantContextOption { AssistantId = assistant.Id, Key = "region", Value = "us" });
        context.AssistantConversationStarters.Add(new AssistantConversationStarter { AssistantId = assistant.Id, Prompt = "Hi", OrderIndex = 0 });
        context.AssistantFiles.Add(new AssistantFile
        {
            AssistantId = assistant.Id,
            FolderKind = "CodeInterpreter",
            RelativePath = "a.txt",
            ContentType = "text/plain",
            Created = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);
        var details = await service.GetAssistantAsync(assistant.Id);

        details.Should().NotBeNull();
        details!.Instructions.Should().Be("be helpful");
        details.ContextOptions.Should().ContainSingle(c => c.Key == "region" && c.Value == "us");
        details.ConversationStarters.Should().ContainSingle(c => c.Prompt == "Hi");
        details.Files.Should().ContainSingle(f => f.RelativePath == "a.txt");
    }

    // ----- Create assistant -----

    [TestMethod]
    public async Task CreateAssistantAsync_PersistsAssistant()
    {
        await using var context = NewContext("create-assistant");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateAssistantDto("New Assistant");
        var created = await service.CreateAssistantAsync(dto);

        created.Name.Should().Be("New Assistant");
        (await service.GetAssistantAsync(created.Id)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task CreateAssistantAsync_Throws_for_invalid_SamplingParametersJson()
    {
        await using var context = NewContext("create-assistant-badjson");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var dto = MinimalCreateAssistantDto("A") with
        {
            ModelId = "x",
            SamplingParametersJson = "{not-json"
        };

        var act = async () => await service.CreateAssistantAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid JSON object*");
    }

    // ----- Delete -----

    [TestMethod]
    public async Task DeleteGuideAsync_ReturnsTrueWhenDeleted_FalseWhenMissing()
    {
        await using var context = NewContext("delete-guide");
        var guide = new Assistant { Name = "Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow };
        context.Assistants.Add(guide);
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        (await service.DeleteGuideAsync(guide.Id)).Should().BeTrue();
        (await service.DeleteGuideAsync(guide.Id)).Should().BeFalse();
    }

    [TestMethod]
    public async Task DeleteAssistantAsync_ReturnsTrueWhenDeleted_FalseWhenMissing()
    {
        await using var context = NewContext("delete-assistant");
        var assistant = new Assistant { Name = "A", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow };
        context.Assistants.Add(assistant);
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        (await service.DeleteAssistantAsync(assistant.Id)).Should().BeTrue();
        (await service.DeleteAssistantAsync(assistant.Id)).Should().BeFalse();
    }

    [TestMethod]
    public async Task DuplicateGuideAsync_Throws_not_implemented()
    {
        await using var context = NewContext("duplicate-guide");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.DuplicateGuideAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    // ----- Operations / preview -----

    [TestMethod]
    public async Task PreviewToolDefinitionAsync_ReturnsToolDefinition_ForValidFragment()
    {
        await using var context = NewContext("preview-ok");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var result = await service.PreviewToolDefinitionAsync(
            new PreviewToolDefinitionDto(OperationFragment, WebApiSpec()));

        result.ToolDefinition.Should().Contain("listItems");
        result.SourceKind.Should().Be("web-api");
        result.ActionType.Should().Be("WebApi");
    }

    private static string WebApiSpec() => """
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

    [TestMethod]
    public async Task PreviewToolDefinitionAsync_Throws_for_invalid_fragment()
    {
        await using var context = NewContext("preview-bad");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.PreviewToolDefinitionAsync(
            new PreviewToolDefinitionDto("{ not json", "{}"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to preview tool definition*");
    }

    [TestMethod]
    public async Task UpdateOperationAsync_Succeeds_ForValidFragment()
    {
        await using var context = NewContext("update-op-ok");
        var operationId = await SeedOperationAsync(context);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var updated = await service.UpdateOperationAsync(operationId, new UpdateOperationDto(OperationFragment));

        updated.Method.Should().Be("GET");
        updated.Path.Should().Be("/items");
        updated.Summary.Should().Be("List items");
    }

    // ----- ValidateRuntimeCompatibilityAsync -----

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_ReturnsValid_ForEmptyMembers()
    {
        await using var context = NewContext("runtime-empty");
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var result = await service.ValidateRuntimeCompatibilityAsync(
            new GuideRuntimeValidationRequest(new List<GuideRuntimeValidationMember>()));

        result.IsValid.Should().BeTrue();
        result.Profiles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_ReturnsValid_WhenNoLocalModels()
    {
        await using var context = NewContext("runtime-no-local");
        context.Models.Add(new Model { ModelId = "cloud-x", DisplayName = "Cloud X", Provider = "openai-chat" });
        await context.SaveChangesAsync();
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var result = await service.ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest(
            new List<GuideRuntimeValidationMember>
            {
                new("assistant", Guid.NewGuid(), "Cloud", "cloud-x")
            }));

        result.IsValid.Should().BeTrue();
        result.Profiles.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_AddsProfile_ForLocalModel()
    {
        await using var context = NewContext("runtime-local-ok");
        context.Models.Add(new Model
        {
            ModelId = "local-x",
            DisplayName = "Local X",
            Provider = "llama-cpp",
            RuntimeConfigJson = """{"routerModelId":"router","runtimeProfileId":"profile-a"}"""
        });
        await context.SaveChangesAsync();

        var resolver = new Mock<IRuntimeProfileResolver>();
        resolver.Setup(r => r.ResolveAsync("profile-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RuntimeProfileData(
                "profile-a",
                CombineSystemAndDeveloperMessages: false,
                ThoughtBlockPattern: null,
                SamplingParameters: new Dictionary<string, SamplingParameterDefinition>(),
                ThinkingControl: new ThinkingControl("None", new Dictionary<string, IReadOnlyList<ThinkingAction>>())));

        var service = GuidesServiceTestHelper.CreateGuidesService(context, resolver.Object);

        var result = await service.ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest(
            new List<GuideRuntimeValidationMember>
            {
                new("assistant", Guid.NewGuid(), "Local", "local-x")
            }));

        result.Profiles.Should().Contain("profile-a");
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_AddsWarning_ForInvalidRuntimeConfig()
    {
        await using var context = NewContext("runtime-local-bad");
        context.Models.Add(new Model
        {
            ModelId = "local-x",
            DisplayName = "Local X",
            Provider = "llama-cpp",
            RuntimeConfigJson = null
        });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var result = await service.ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest(
            new List<GuideRuntimeValidationMember>
            {
                new("assistant", Guid.NewGuid(), "Local", "local-x")
            }));

        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("invalid local runtime configuration");
    }

    // ----- helpers -----

    private static ApplicationDbContext NewContext(string name) =>
        new(BackgroundJobTestHelpers.CreateInMemoryOptions($"{name}-{Guid.NewGuid():N}"));

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
            SpecificationJson = "{}"
        };
        context.AssistantOpenApiSchemas.Add(schema);
        var operation = new AssistantOpenApiOperation
        {
            Id = Guid.NewGuid(),
            SchemaId = schema.Id,
            OperationId = "listItems",
            Method = "GET",
            Path = "/items",
            Summary = "old",
            ToolDefinitionJson = "{}",
            SchemaFragmentJson = "{}",
            Created = DateTime.UtcNow
        };
        context.AssistantOpenApiOperations.Add(operation);
        await context.SaveChangesAsync();
        return operation.Id;
    }

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
}
