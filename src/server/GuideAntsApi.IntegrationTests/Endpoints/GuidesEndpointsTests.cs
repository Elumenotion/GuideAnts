using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class GuidesEndpointsTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        SetupAuthentication();
    }

    [TestMethod]
    public async Task GetGuides_Requires_admin_authorization()
    {
        Client.DefaultRequestHeaders.Authorization = null;

        var response = await Client.GetAsync("/api/guides");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Guide_crud_round_trip_through_api()
    {
        var createDto = new CreateGuideDto(
            Name: $"Guide {Guid.NewGuid():N}",
            Description: "Integration guide",
            Instructions: "Be helpful",
            HomePageMarkdown: "# Welcome",
            ModelId: "gpt-4.1",
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
            ConversationStarters: ["Hello"],
            CrewMemberIds: null);

        var createResponse = await Client.PostAsJsonAsync("/api/guides", createDto);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<GuideDto>();
        created.Should().NotBeNull();
        created!.Name.Should().Be(createDto.Name);

        var getResponse = await Client.GetAsync($"/api/guides/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var details = await getResponse.Content.ReadFromJsonAsync<GuideDetailsDto>();
        details.Should().NotBeNull();
        details!.Guide.Id.Should().Be(created.Id);

        var updateDto = createDto with { Name = $"{createDto.Name} Updated", Description = "Updated description" };
        var updateResponse = await Client.PutAsJsonAsync($"/api/guides/{created.Id}", updateDto);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<GuideDto>();
        updated!.Name.Should().EndWith("Updated");

        var listResponse = await Client.GetAsync("/api/guides");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var guides = await listResponse.Content.ReadFromJsonAsync<List<GuideDto>>();
        guides.Should().Contain(g => g.Id == created.Id);

        var deleteResponse = await Client.DeleteAsync($"/api/guides/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var missingResponse = await Client.GetAsync($"/api/guides/{created.Id}");
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ValidateGuideRuntime_Returns_ok_for_cloud_model()
    {
        var request = new GuideRuntimeValidationRequest(
        [
            new GuideRuntimeValidationMember("guide", EntityId: null, DisplayName: "Test Guide", ModelId: "gpt-4.1")
        ]);

        var response = await Client.PostAsJsonAsync("/api/guides/runtime/validate", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<GuideRuntimeValidationDto>();
        result.Should().NotBeNull();
        result!.IsValid.Should().BeTrue();
    }
}
