using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.SystemGuide;
using GuideAntsApi.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Tests.BackgroundJobs;
using Moq;

namespace GuideAntsApi.Tests.Services.Guides;

[TestClass]
public sealed class GuidesServiceTests
{
    [TestMethod]
    public async Task GetGuidesAsync_Returns_only_guide_kind_assistants()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-list-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        context.Assistants.AddRange(
            new Assistant { Name = "Guide One", Kind = AssistantKind.Guide, Created = DateTime.UtcNow },
            new Assistant { Name = "Assistant One", Kind = AssistantKind.Assistant, Created = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var guides = (await service.GetGuidesAsync()).ToList();

        guides.Should().ContainSingle();
        guides[0].Name.Should().Be("Guide One");
    }

    [TestMethod]
    public async Task GetGuidesAsync_Excludes_system_guide_ids_from_settings()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-system-filter-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var systemGuideId = Guid.NewGuid();
        var regularGuideId = Guid.NewGuid();
        context.Assistants.AddRange(
            new Assistant { Id = systemGuideId, Name = "GuideAnts Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow },
            new Assistant { Id = regularGuideId, Name = "Team Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var store = new Mock<GuideAntsApi.Settings.IGuideAntsSystemSettingsStore>();
        store.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuideAntsApi.Settings.GuideAntsSystemSettings { UserGuideId = systemGuideId });

        var service = GuidesServiceTestHelper.CreateGuidesService(
            context,
            catalogFilter: new SystemGuideCatalogFilter(store.Object, context));

        var guides = (await service.GetGuidesAsync()).ToList();

        guides.Should().ContainSingle();
        guides[0].Id.Should().Be(regularGuideId);
    }

    [TestMethod]
    public async Task GetGuidesAsync_Includes_system_guides_for_system_project()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-system-include-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var systemGuideId = Guid.NewGuid();
        var systemProjectId = Guid.NewGuid();
        context.Projects.Add(new Project
        {
            Id = systemProjectId,
            Title = "GuideAnts System",
            IsSystemProject = true
        });
        context.Assistants.AddRange(
            new Assistant { Id = systemGuideId, Name = "GuideAnts Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow },
            new Assistant { Name = "Team Guide", Kind = AssistantKind.Guide, Created = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var store = new Mock<IGuideAntsSystemSettingsStore>();
        store.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuideAntsSystemSettings { UserGuideId = systemGuideId });

        var service = GuidesServiceTestHelper.CreateGuidesService(
            context,
            catalogFilter: new SystemGuideCatalogFilter(store.Object, context));

        var guides = (await service.GetGuidesAsync(systemProjectId)).ToList();

        guides.Should().HaveCount(2);
        guides.Select(g => g.Id).Should().Contain(systemGuideId);
    }

    [TestMethod]
    public async Task GetGuideAsync_Returns_null_when_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-missing-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var guide = await service.GetGuideAsync(Guid.NewGuid());

        guide.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateGuideAsync_UpdateGuideAsync_and_DeleteGuideAsync_round_trip()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-crud-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var created = await service.CreateGuideAsync(MinimalCreateGuideDto("Original Guide"));
        created.Name.Should().Be("Original Guide");

        var updated = await service.UpdateGuideAsync(created.Id, MinimalUpdateGuideDto("Updated Guide"));
        updated.Name.Should().Be("Updated Guide");

        var details = await service.GetGuideAsync(created.Id);
        details.Should().NotBeNull();
        details!.Guide.Name.Should().Be("Updated Guide");

        (await service.DeleteGuideAsync(created.Id)).Should().BeTrue();
        (await service.GetGuideAsync(created.Id)).Should().BeNull();
        (await service.DeleteGuideAsync(created.Id)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Assistant_crud_and_listing_work_for_non_guide_assistants()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"assistants-crud-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var created = await service.CreateAssistantAsync(MinimalCreateAssistantDto("Crew Member"));
        var listed = (await service.GetAssistantsAsync()).ToList();
        listed.Should().ContainSingle(a => a.Id == created.Id);

        var updated = await service.UpdateAssistantAsync(created.Id, MinimalUpdateAssistantDto("Renamed Crew"));
        updated.Name.Should().Be("Renamed Crew");

        (await service.DeleteAssistantAsync(created.Id)).Should().BeTrue();
        (await service.GetAssistantAsync(created.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task Guide_environment_is_scoped_by_project()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guide-env-project-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var projectA = new Project { Title = "Project A", Slug = "project-a" };
        var projectB = new Project { Title = "Project B", Slug = "project-b" };
        context.Projects.AddRange(projectA, projectB);
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);
        var created = await service.CreateGuideAsync(MinimalCreateGuideDto("Env Guide") with
        {
            ProjectId = projectA.Id,
            EnvironmentVariables = [new EnvironmentVariableDto("API_MODE", "project-a", false)]
        });

        var projectADetails = await service.GetGuideAsync(created.Id, projectA.Id);
        var projectBDetails = await service.GetGuideAsync(created.Id, projectB.Id);
        var unscopedDetails = await service.GetGuideAsync(created.Id);

        projectADetails!.EnvironmentVariables.Should()
            .ContainSingle()
            .Which.Should().BeEquivalentTo(new EnvironmentVariableDto("API_MODE", "project-a", false));
        projectBDetails!.EnvironmentVariables.Should().BeNull();
        unscopedDetails!.EnvironmentVariables.Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_Returns_valid_for_empty_members()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-validate-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var result = await service.ValidateRuntimeCompatibilityAsync(new GuideRuntimeValidationRequest([]));

        result.IsValid.Should().BeTrue();
        result.Conflicts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DuplicateGuideAsync_Throws_not_implemented()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-dup-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.DuplicateGuideAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotImplementedException>();
    }

    [TestMethod]
    public async Task PreviewToolDefinitionAsync_Returns_serialized_tool_definition()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-preview-tool-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var fragment = """
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

        var result = await service.PreviewToolDefinitionAsync(new PreviewToolDefinitionDto(fragment, WebApiSpec()));

        result.ToolDefinition.Should().Contain("listItems");
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
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-preview-invalid-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = GuidesServiceTestHelper.CreateGuidesService(context);

        var act = async () => await service.PreviewToolDefinitionAsync(
            new PreviewToolDefinitionDto("{ invalid", "{}"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to preview tool definition*");
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_Resolves_local_llama_profiles()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-runtime-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        context.Models.Add(new Model
        {
            ModelId = "local-model",
            DisplayName = "Local Model",
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
        var request = new GuideRuntimeValidationRequest([
            new GuideRuntimeValidationMember("guide", null, "Guide", "local-model")
        ]);

        var result = await service.ValidateRuntimeCompatibilityAsync(request);

        result.IsValid.Should().BeTrue();
        result.Profiles.Should().ContainSingle().Which.Should().Be("profile-a");
    }

    [TestMethod]
    public async Task ValidateRuntimeCompatibilityAsync_Adds_warning_for_invalid_runtime_config()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-runtime-warn-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        context.Models.Add(new Model
        {
            ModelId = "broken-local",
            DisplayName = "Broken Local",
            Provider = "llama-cpp",
            RuntimeConfigJson = null
        });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);
        var request = new GuideRuntimeValidationRequest([
            new GuideRuntimeValidationMember("guide", null, "Guide", "broken-local")
        ]);

        var result = await service.ValidateRuntimeCompatibilityAsync(request);

        result.Warnings.Should().ContainSingle(w => w.Contains("invalid local runtime configuration"));
    }

    [TestMethod]
    public async Task CreateGuideAsync_With_custom_tools_context_and_auth_round_trips_in_get_guide()
    {
        const string openApiSpec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Example API", "version": "1.0.0" },
              "servers": [{ "url": "https://api.example.com" }],
              "paths": {
                "/items": {
                  "get": {
                    "operationId": "listItems",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-rich-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var crewMember = new Assistant
        {
            Name = "Crew",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow
        };
        context.Assistants.Add(crewMember);
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);
        var created = await service.CreateGuideAsync(MinimalCreateGuideDto("Rich Guide") with
        {
            CustomTools =
            [
                new CustomToolDto(
                    "ExampleApi",
                    openApiSpec,
                    "https://api.example.com",
                    new OpenApiAuthConfigDto(
                        AuthType: "apiKey",
                        ClientId: null,
                        Tenant: null,
                        Scopes: ["read"],
                        ValueTemplate: "secret",
                        HeaderName: "X-Api-Key",
                        UserConfigPolicy: "optional"))
            ],
            ContextOptions = [new ContextOptionDto("region", "us-east")],
            AuthProviders =
            [
                new AuthProviderDto(
                    Id: null,
                    ProviderId: "example",
                    AuthType: "apiKey",
                    ClientId: null,
                    Tenant: null,
                    HeaderName: "X-Api-Key",
                    ValueTemplate: "secret",
                    UserConfigPolicy: "optional",
                    Scopes: null)
            ],
            ConversationStarters = ["Hello"],
            CrewMemberIds = [crewMember.Id]
        });

        var details = await service.GetGuideAsync(created.Id);

        details.Should().NotBeNull();
        details!.CustomTools.Should().ContainSingle(t => t.Name == "ExampleApi");
        details.ContextOptions.Should().ContainSingle(o => o.Key == "region");
        details.AuthProviders.Should().ContainSingle(p => p.ProviderId == "example");
        details.ConversationStarters.Should().ContainSingle(s => s.Prompt == "Hello");
        details.Crews.Should().ContainSingle(c => c.Members.Any(m => m.AssistantId == crewMember.Id));
    }

    [TestMethod]
    public async Task CreateGuideAsync_Throws_when_sampling_json_invalid()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"guides-sampling-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        context.Models.Add(new Model
        {
            ModelId = "gpt-4.1",
            DisplayName = "GPT",
            Provider = "openai-chat"
        });
        await context.SaveChangesAsync();

        var service = GuidesServiceTestHelper.CreateGuidesService(context);
        var dto = MinimalCreateGuideDto("Sampling Guide") with
        {
            ModelId = "gpt-4.1",
            SamplingParametersJson = "not-json"
        };

        var act = async () => await service.CreateGuideAsync(dto);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SamplingParametersJson*");
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
