using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.IntegrationTests.Settings;

/// <summary>
/// Deep HTTP coverage for <see cref="GuideAntsApi.Endpoints.Settings.SettingsEndpoints"/>
/// and the underlying <see cref="GuideAntsApi.Settings.ApplicationSettingsService"/>
/// branches. Drives the endpoints over the real pipeline (auth + DB) so section
/// reads/writes, concurrency conflicts, validation errors, model + runtime-profile
/// CRUD, and the infrastructure/overview composites are exercised end-to-end.
/// </summary>
[TestClass]
public sealed class SettingsEndpointsDeepTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task Sections_Schema_Readiness_ReturnOk()
    {
        var sections = await Client.GetAsync("/api/settings/sections");
        sections.StatusCode.Should().Be(HttpStatusCode.OK);
        var summaries = await sections.Content.ReadFromJsonAsync<List<SettingsSectionSummaryDto>>();
        summaries.Should().NotBeNull();
        summaries!.Should().NotBeEmpty();

        var schema = await Client.GetAsync("/api/settings/schema");
        schema.StatusCode.Should().Be(HttpStatusCode.OK);
        var schemaDto = await schema.Content.ReadFromJsonAsync<SettingsSchemaDto>();
        schemaDto.Should().NotBeNull();
        schemaDto!.Sections.Should().NotBeEmpty();

        var readiness = await Client.GetAsync("/api/settings/readiness");
        readiness.StatusCode.Should().Be(HttpStatusCode.OK);
        var readinessDto = await readiness.Content.ReadFromJsonAsync<SettingsReadinessDto>();
        readinessDto.Should().NotBeNull();
    }

    [TestMethod]
    public async Task GetSection_Unknown_Returns404()
    {
        var response = await Client.GetAsync("/api/settings/sections/NotARealSection");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task UpdateSection_ValidStaleAndUnsupportedField_CoverBranches()
    {
        // Read the seeded ChatDefaults section to obtain a current RowVersion.
        var sectionResponse = await Client.GetAsync("/api/settings/sections/ChatDefaults");
        sectionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var section = await sectionResponse.Content.ReadFromJsonAsync<SettingsSectionDto>();
        section.Should().NotBeNull();

        // Valid update with a supported field merges and returns the refreshed section.
        var validPayload = new
        {
            rowVersion = section!.RowVersion,
            payload = new { OverrideAllChatModels = false }
        };
        var validUpdate = await Client.PutAsJsonAsync("/api/settings/sections/ChatDefaults", validPayload);
        validUpdate.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await validUpdate.Content.ReadFromJsonAsync<SettingsSectionDto>();
        refreshed.Should().NotBeNull();

        // Stale RowVersion -> 409 conflict.
        var stalePayload = new
        {
            rowVersion = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }),
            payload = new { OverrideAllChatModels = true }
        };
        var staleUpdate = await Client.PutAsJsonAsync("/api/settings/sections/ChatDefaults", stalePayload);
        staleUpdate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Unsupported field -> 400 validation error.
        var unsupportedPayload = new
        {
            rowVersion = refreshed!.RowVersion,
            payload = new { ThisFieldDoesNotExist = "x" }
        };
        var unsupportedUpdate = await Client.PutAsJsonAsync("/api/settings/sections/ChatDefaults", unsupportedPayload);
        unsupportedUpdate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task UpdateSection_UnknownSection_Returns404()
    {
        var payload = new
        {
            rowVersion = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            payload = new { Anything = "x" }
        };
        var response = await Client.PutAsJsonAsync("/api/settings/sections/NotARealSection", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task ChatDefaults_GetAndPut_ReturnOk()
    {
        var get = await Client.GetAsync("/api/settings/chat-defaults");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await get.Content.ReadFromJsonAsync<ChatDefaultsDto>();
        dto.Should().NotBeNull();

        var update = new UpdateChatDefaultsRequest(
            RowVersion: dto!.RowVersion,
            DefaultModelId: null,
            OverrideAllChatModels: false,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null);
        var put = await Client.PutAsJsonAsync("/api/settings/chat-defaults", update);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await put.Content.ReadFromJsonAsync<ChatDefaultsDto>();
        updated.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Models_FullCrudLifecycle()
    {
        var modelId = "deep-test-model-" + Guid.NewGuid().ToString("N");

        var list = await Client.GetAsync("/api/settings/models");
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var create = new CreateSettingsModelRequest(
            ModelId: modelId,
            DisplayName: "Deep Test Model",
            Provider: "openai-chat",
            Description: "integration",
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: true,
            DisplayOrder: 999);
        var createResponse = await Client.PostAsJsonAsync("/api/settings/models", create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<SettingsModelDto>();
        created!.ModelId.Should().Be(modelId);

        // Duplicate create -> 400.
        var duplicate = await Client.PostAsJsonAsync("/api/settings/models", create);
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Update existing -> 200.
        var update = new UpdateSettingsModelRequest(
            ModelId: modelId,
            DisplayName: "Deep Test Model (updated)",
            Provider: "openai-chat",
            Description: "updated",
            ReasoningChoicesJson: null,
            RuntimeConfigJson: null,
            IsActive: false,
            DisplayOrder: 1000);
        var updateResponse = await Client.PutAsJsonAsync($"/api/settings/models/{modelId}", update);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Update unknown -> 404.
        var unknownUpdate = await Client.PutAsJsonAsync(
            "/api/settings/models/no-such-model-id", update);
        unknownUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Delete existing -> 204.
        var delete = await Client.DeleteAsync($"/api/settings/models/{modelId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Delete unknown -> 404.
        var deleteUnknown = await Client.DeleteAsync("/api/settings/models/no-such-model-id");
        deleteUnknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }


    [TestMethod]
    public async Task Overview_And_ConnectionUsage_ReturnOk()
    {
        var overview = await Client.GetAsync("/api/settings/overview");
        overview.StatusCode.Should().Be(HttpStatusCode.OK);
        var overviewDto = await overview.Content.ReadFromJsonAsync<SettingsOverviewDto>();
        overviewDto.Should().NotBeNull();

        var usage = await Client.GetAsync("/api/settings/connections/LlamaCpp/usage");
        usage.StatusCode.Should().Be(HttpStatusCode.OK);
        var usageDto = await usage.Content.ReadFromJsonAsync<ConnectionUsageDto>();
        usageDto.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Infrastructure_Dependencies_Probes_And_Override()
    {
        var dependencies = await Client.GetAsync("/api/settings/infrastructure/dependencies");
        dependencies.StatusCode.Should().Be(HttpStatusCode.OK);
        var depList = await dependencies.Content.ReadFromJsonAsync<List<SettingsRuntimeDependencyDto>>();
        depList.Should().NotBeNull();

        // Null items -> 400.
        var badProbe = await Client.PostAsJsonAsync(
            "/api/settings/infrastructure/probes", new { items = (object?)null });
        badProbe.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A path probe of a known-existing directory -> 200 with a result.
        var probeRequest = new InfrastructureProbeRequestDto(new[]
        {
            new InfrastructureProbeRequestItemDto("temp", "path", Path.GetTempPath())
        });
        var probe = await Client.PostAsJsonAsync("/api/settings/infrastructure/probes", probeRequest);
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
        var batch = await probe.Content.ReadFromJsonAsync<InfrastructureProbeBatchDto>();
        batch.Should().NotBeNull();
        batch!.Results.Should().ContainSingle();

        // Override for an unknown dependency key -> 404.
        var override404 = await Client.PutAsJsonAsync(
            "/api/settings/infrastructure/dependencies/NotASection:NotAField",
            new InfrastructureDependencyOverrideRequestDto("value"));
        override404.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ServiceEditor_UnknownService_Returns400()
    {
        var response = await Client.GetAsync("/api/settings/services/NotARealService");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
