using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
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

    [TestMethod]
    public async Task ExportGuide_Requires_admin_authorization()
    {
        var guideId = Guid.NewGuid();
        Client.DefaultRequestHeaders.Authorization = null;

        var response = await Client.GetAsync($"/api/guides/{guideId}/export");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task ExportGuide_Returns_bad_request_for_missing_guide()
    {
        var response = await Client.GetAsync($"/api/guides/{Guid.NewGuid()}/export");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Guide not found");
    }

    [TestMethod]
    public async Task ExportGuide_Returns_zip_with_manifest_behavior()
    {
        var createDto = new CreateGuideDto(
            Name: $"Export Guide {Guid.NewGuid():N}",
            Description: "Integration export",
            Instructions: "Export instructions",
            HomePageMarkdown: "# Home",
            ModelId: "gpt-4.1",
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null,
            AvatarImageBytes: null,
            AvatarContentType: null,
            ToolIds: null,
            CustomTools: null,
            ContextOptions:
            [
                new ContextOptionDto("audience", "engineering"),
                new ContextOptionDto("locale", "en-US")
            ],
            AuthProviders: null,
            Files: null,
            ConversationStarters: ["Start", "Investigate"],
            CrewMemberIds: null);

        var createResponse = await Client.PostAsJsonAsync("/api/guides", createDto);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<GuideDto>();
        created.Should().NotBeNull();

        var exportResponse = await Client.GetAsync($"/api/guides/{created!.Id}/export");

        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        exportResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/zip");
        var bytes = await exportResponse.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json");
        manifestEntry.Should().NotBeNull();
        using var reader = new StreamReader(manifestEntry!.Open());
        var manifestJson = await reader.ReadToEndAsync();
        var manifest = JsonDocument.Parse(manifestJson).RootElement;
        manifest.GetProperty("name").GetString().Should().Be(createDto.Name);
        manifest.GetProperty("description").GetString().Should().Be(createDto.Description);

        var instructionsEntry = archive.GetEntry("instructions.md");
        instructionsEntry.Should().NotBeNull();
        using (var instructionsReader = new StreamReader(instructionsEntry!.Open()))
        {
            (await instructionsReader.ReadToEndAsync()).Should().Be(createDto.Instructions);
        }

        var homeEntry = archive.GetEntry("HostExtensions/UI/home.md");
        homeEntry.Should().NotBeNull();
        using (var homeReader = new StreamReader(homeEntry!.Open()))
        {
            (await homeReader.ReadToEndAsync()).Should().Be(createDto.HomePageMarkdown);
        }

        var startersEntry = archive.GetEntry("HostExtensions/UI/conversationStarters.json");
        startersEntry.Should().NotBeNull();
        using (var startersReader = new StreamReader(startersEntry!.Open()))
        {
            var starters = JsonDocument.Parse(await startersReader.ReadToEndAsync())
                .RootElement
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList();
            starters.Should().Equal(["Start", "Investigate"]);
        }

        var contextEntry = archive.GetEntry("HostExtensions/UI/contextOptions.json");
        contextEntry.Should().NotBeNull();
        using (var contextReader = new StreamReader(contextEntry!.Open()))
        {
            var contextOptions = JsonDocument.Parse(await contextReader.ReadToEndAsync())
                .RootElement
                .EnumerateArray()
                .Select(item => new
                {
                    Key = item.GetProperty("key").GetString(),
                    Value = item.GetProperty("value").GetString()
                })
                .ToList();

            contextOptions.Should().Contain(option => option.Key == "audience" && option.Value == "engineering");
            contextOptions.Should().Contain(option => option.Key == "locale" && option.Value == "en-US");
        }
    }
}
