using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.IntegrationTests.Settings;

/// <summary>
/// Regression: after persisting <c>ChatDefaults</c> via the settings API, chat readiness must
/// resolve the new default immediately (same request pipeline / store), not only after a
/// configuration reload.
/// </summary>
[TestClass]
public sealed class ChatDefaultsChatReadinessIntegrationTests : BaseEndpointTest
{
    private const string DefaultModelId = "gpt-4.1";

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task PutChatDefaults_ImmediatelyReflectsInNotebookChatReadiness()
    {
        var projectResponse = await Client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto($"chat-defaults-readiness-{Guid.NewGuid():N}", "Chat defaults readiness"));
        projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await projectResponse.Content.ReadFromJsonAsync<ProjectDto>();
        project.Should().NotBeNull();

        var notebookResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{project!.Id}/notebooks",
            new { title = $"nb-{Guid.NewGuid():N}", guideId = await GetDefaultGuideIdAsync() });
        notebookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var notebook = await notebookResponse.Content.ReadFromJsonAsync<NotebookDto>();
        notebook.Should().NotBeNull();

        var getDefaults = await Client.GetAsync("/api/settings/chat-defaults");
        getDefaults.StatusCode.Should().Be(HttpStatusCode.OK);
        var defaults = await getDefaults.Content.ReadFromJsonAsync<ChatDefaultsDto>();
        defaults.Should().NotBeNull();

        var update = new UpdateChatDefaultsRequest(
            RowVersion: defaults!.RowVersion,
            DefaultModelId: DefaultModelId,
            OverrideAllChatModels: true,
            Temperature: null,
            TopP: null,
            ReasoningEffort: null,
            SamplingParametersJson: null);
        var put = await Client.PutAsJsonAsync("/api/settings/chat-defaults", update);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var readinessResponse = await Client.GetAsync(
            $"/api/notebooks/{notebook!.Id}/header-toolbar/chat-readiness");
        readinessResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var readiness = await readinessResponse.Content.ReadFromJsonAsync<NotebookChatReadinessDto>();
        readiness.Should().NotBeNull();
        readiness!.EffectiveModelId.Should().Be(DefaultModelId);
    }
}
