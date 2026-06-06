using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class AuthorizationMatrixEndpointsTests : BaseEndpointTest
{
    private static readonly Guid MatrixUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string MatrixUserEmail = "matrix.user@example.com";
    private const string MatrixUserName = "Matrix User";

    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        UseRole(Role.Admin);
    }

    [TestMethod]
    public async Task RepresentativeEndpointGuards_MatchRoleMatrixAndToolbarSplit()
    {
        UseRole(Role.Admin);
        var seedProjectResponse = await Client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto($"matrix-seed-{Guid.NewGuid():N}", "Authorization matrix seed"));
        seedProjectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var seedProject = await seedProjectResponse.Content.ReadFromJsonAsync<ProjectDto>();
        seedProject.Should().NotBeNull();

        var seedNotebookResponse = await Client.PostAsJsonAsync(
            $"/api/projects/{seedProject!.Id}/notebooks",
            new
            {
                title = $"matrix-notebook-{Guid.NewGuid():N}",
                guideId = await GetDefaultGuideIdAsync()
            });
        seedNotebookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var seedNotebook = await seedNotebookResponse.Content.ReadFromJsonAsync<NotebookDto>();
        seedNotebook.Should().NotBeNull();

        // RequireApprovedUser representative endpoint.
        UseRole(Role.Reader);
        var approvedReaderResponse = await Client.GetAsync("/api/users/current");
        approvedReaderResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        UseRole(Role.Pending);
        var approvedPendingResponse = await Client.GetAsync("/api/users/current");
        approvedPendingResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ClearAuthentication();
        var approvedAnonymousResponse = await Client.GetAsync("/api/users/current");
        approvedAnonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // RequireContributor representative endpoint.
        UseRole(Role.Contributor);
        var contributorMutationResponse = await Client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto($"matrix-contributor-{Guid.NewGuid():N}", "Contributor write"));
        contributorMutationResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        UseRole(Role.Reader);
        var readerMutationResponse = await Client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto($"matrix-reader-{Guid.NewGuid():N}", "Reader write"));
        readerMutationResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        UseRole(Role.Pending);
        var pendingMutationResponse = await Client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto($"matrix-pending-{Guid.NewGuid():N}", "Pending write"));
        pendingMutationResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ClearAuthentication();
        var anonymousMutationResponse = await Client.PostAsJsonAsync(
            "/api/projects",
            new CreateProjectDto($"matrix-anon-{Guid.NewGuid():N}", "Anonymous write"));
        anonymousMutationResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // RequireAdmin representative endpoint.
        UseRole(Role.Admin);
        var adminListResponse = await Client.GetAsync("/api/admin/users");
        adminListResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        UseRole(Role.Contributor);
        var contributorListResponse = await Client.GetAsync("/api/admin/users");
        contributorListResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        UseRole(Role.Reader);
        var readerListResponse = await Client.GetAsync("/api/admin/users");
        readerListResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        UseRole(Role.Pending);
        var pendingListResponse = await Client.GetAsync("/api/admin/users");
        pendingListResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        ClearAuthentication();
        var anonymousListResponse = await Client.GetAsync("/api/admin/users");
        anonymousListResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Notebook toolbar split.
        UseRole(Role.Admin);
        var adminToolbarResponse = await Client.GetAsync($"/api/notebooks/{seedNotebook!.Id}/header-toolbar");
        adminToolbarResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        UseRole(Role.Contributor);
        var contributorToolbarResponse = await Client.GetAsync($"/api/notebooks/{seedNotebook.Id}/header-toolbar");
        contributorToolbarResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var contributorReadinessResponse = await Client.GetAsync($"/api/notebooks/{seedNotebook.Id}/header-toolbar/chat-readiness");
        contributorReadinessResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        UseRole(Role.Reader);
        var readerReadinessResponse = await Client.GetAsync($"/api/notebooks/{seedNotebook.Id}/header-toolbar/chat-readiness");
        readerReadinessResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        UseRole(Role.Pending);
        var pendingReadinessResponse = await Client.GetAsync($"/api/notebooks/{seedNotebook.Id}/header-toolbar/chat-readiness");
        pendingReadinessResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private void UseRole(Role role)
    {
        SetupAuthentication(
            role,
            email: MatrixUserEmail,
            name: MatrixUserName,
            userId: MatrixUserId);
    }

    private void ClearAuthentication()
    {
        Client.DefaultRequestHeaders.Authorization = null;
    }
}
