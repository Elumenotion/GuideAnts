using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class AssistantsEndpointsTests : BaseEndpointTest
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
    public async Task Assistant_crud_round_trip_through_api()
    {
        var createDto = new CreateAssistantDto(
            Name: $"Assistant {Guid.NewGuid():N}",
            Description: "Crew member",
            Instructions: "Execute tasks",
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
            Files: null,
            ConversationStarters: null);

        var createResponse = await Client.PostAsJsonAsync("/api/assistants", createDto);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<AssistantDto>();
        created.Should().NotBeNull();

        var getResponse = await Client.GetAsync($"/api/assistants/{created!.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateDto = new UpdateAssistantDto(
            Name: $"{createDto.Name} Updated",
            Description: "Updated",
            Instructions: createDto.Instructions,
            ModelId: createDto.ModelId,
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

        var updateResponse = await Client.PutAsJsonAsync($"/api/assistants/{created.Id}", updateDto);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await Client.DeleteAsync($"/api/assistants/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
