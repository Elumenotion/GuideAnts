using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class PublishedGuidesAppIdentityEndpointsTests : BaseEndpointTest
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

    protected override async Task CleanDatabaseAsync()
    {
        if (SharedFactory != null)
        {
            using var scope = SharedFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
        }

        await base.CleanDatabaseAsync();
    }

    [TestMethod]
    public async Task GetPublishedGuide_Returns_authMode_and_requiresAuth_for_app_identity()
    {
        var pubId = await SeedAppIdentityPublishedGuideAsync();

        var response = await Client!.GetAsync($"/api/published/guides/{pubId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("authMode").GetString().Should().Be("AppIdentity");
        json.RootElement.GetProperty("requiresAuth").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("requiresApiKey").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task AppIdentity_missing_token_on_message_endpoint_returns_401()
    {
        var seeded = await SeedAppIdentityConversationAsync();
        Client!.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/published/projects/{seeded.ProjectId}/notebooks/{seeded.NotebookId}/conversations/{seeded.ConversationId}/messages?pubId={seeded.PubId}");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = JsonContent.Create(new { instructions = "hello" });

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task PublishGuide_Rejects_app_identity_auth_mode()
    {
        var guideId = await GetDefaultGuideIdAsync();
        var projectResponse = await Client!.PostAsJsonAsync(
            "/api/projects",
            new GuideAntsApi.Models.CreateProjectDto($"pub-auth-{Guid.NewGuid():N}", "desc"));
        projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await projectResponse.Content.ReadFromJsonAsync<GuideAntsApi.Models.ProjectDto>();

        var response = await Client.PostAsJsonAsync(
            $"/api/guides/{guideId}/publish",
            new PublishGuideDto
            {
                ProjectId = project!.Id,
                AuthMode = PublishedGuideAuthMode.AppIdentity
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Be("app_identity_auth_not_configurable_via_api");
    }

    [TestMethod]
    public async Task UpdatePublishedGuide_Rejects_setting_auth_mode_to_app_identity()
    {
        var guideId = await GetDefaultGuideIdAsync();
        var projectResponse = await Client!.PostAsJsonAsync(
            "/api/projects",
            new GuideAntsApi.Models.CreateProjectDto($"pub-update-{Guid.NewGuid():N}", "desc"));
        projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await projectResponse.Content.ReadFromJsonAsync<GuideAntsApi.Models.ProjectDto>();

        var publishResponse = await Client.PostAsJsonAsync(
            $"/api/guides/{guideId}/publish",
            new PublishGuideDto
            {
                ProjectId = project!.Id,
                AuthMode = PublishedGuideAuthMode.Anonymous
            });
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var publishJson = JsonDocument.Parse(await publishResponse.Content.ReadAsStringAsync());
        var pubId = publishJson.RootElement.GetProperty("id").GetGuid();

        var response = await Client.PutAsJsonAsync(
            $"/api/guides/{guideId}/publish/{pubId}",
            new UpdatePublishedGuideDto
            {
                AuthMode = PublishedGuideAuthMode.AppIdentity,
                DisplayMode = "full"
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetString().Should().Be("app_identity_auth_not_configurable_via_api");
    }

    [TestMethod]
    public async Task UpdatePublishedGuide_Rejects_changing_auth_mode_away_from_app_identity()
    {
        var pubId = await SeedAppIdentityPublishedGuideAsync();
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var published = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .SingleAsync(pg => pg.Id == pubId);

            var response = await Client!.PutAsJsonAsync(
                $"/api/guides/{published.GuideId}/publish/{pubId}",
                new UpdatePublishedGuideDto
                {
                    AuthMode = PublishedGuideAuthMode.Anonymous,
                    DisplayMode = published.DisplayMode
                });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("error").GetString().Should().Be("app_identity_auth_not_configurable_via_api");
        }
    }

    [TestMethod]
    public async Task SendMessageStream_Persists_internal_user_id_for_app_identity()
    {
        FakeChatCompletionBehavior.Instance.Reset();
        var seeded = await SeedAppIdentityConversationWithApprovedUserAsync();

        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == seeded.UserId);
        var jwtService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var token = jwtService.IssueToken(user, Role.Reader).Token;

        var authService = scope.ServiceProvider.GetRequiredService<IPublishedGuideAuthService>();
        var authResult = await authService.ValidateAsync(
            seeded.PubId,
            $"Bearer {token}",
            seeded.ProjectId,
            seeded.NotebookId,
            CancellationToken.None);

        authResult.IsValid.Should().BeTrue();
        authResult.InternalUserId.Should().Be(seeded.UserId);

        var conversationService = scope.ServiceProvider.GetRequiredService<IPublishedConversationService>();
        await foreach (var _ in conversationService.SendMessageStreamAsync(
                           seeded.ConversationId,
                           new GuideAntsApi.Models.Conversations.SendMessageRequest
                           {
                               Instructions = "hello app identity",
                               ModelDeploymentId = "gpt-4.1"
                           },
                           seeded.PubId.ToString(),
                           authResult.UserIdentity,
                           authResult.InternalUserId,
                           CancellationToken.None))
        {
        }

        var userMessage = await db.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == seeded.ConversationId && m.Role == DataModelChatRole.User)
            .SingleAsync();

        userMessage.UserId.Should().Be(seeded.UserId);
        userMessage.ExternalUserIdentity.Should().Be(seeded.UserId.ToString());
    }

    private sealed record SeededConversation(
        Guid ProjectId,
        Guid NotebookId,
        Guid ConversationId,
        Guid PubId,
        Guid UserId);

    private static async Task<Guid> SeedAppIdentityPublishedGuideAsync()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var guideId = await db.Assistants
            .Where(a => a.Kind == AssistantKind.Guide && a.IsActive && a.Name == "Template Guide")
            .Select(a => a.Id)
            .FirstAsync();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = "AppIdentity Project",
            Slug = $"appidentity-{Guid.NewGuid():N}",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            GuideId = guideId,
            Title = "AppIdentity Notebook",
            Slug = $"appidentity-nb-{Guid.NewGuid():N}",
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);

        var pubId = Guid.NewGuid();
        db.PublishedGuides.Add(new PublishedGuide
        {
            Id = pubId,
            GuideId = guideId,
            NotebookId = notebook.Id,
            Active = true,
            AuthMode = PublishedGuideAuthMode.AppIdentity
        });
        await db.SaveChangesAsync();
        return pubId;
    }

    private async Task<SeededConversation> SeedAppIdentityConversationAsync()
    {
        var pubId = await SeedAppIdentityPublishedGuideAsync();
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var published = await db.PublishedGuides
            .Include(pg => pg.Notebook)
            .SingleAsync(pg => pg.Id == pubId);

        var conversation = new NotebookConversation
        {
            NotebookId = published.NotebookId,
            Title = "AppIdentity conversation"
        };
        db.NotebookConversations.Add(conversation);
        await db.SaveChangesAsync();

        return new SeededConversation(
            published.Notebook.ProjectId,
            published.NotebookId,
            conversation.Id,
            pubId,
            Guid.Empty);
    }

    private async Task<SeededConversation> SeedAppIdentityConversationWithApprovedUserAsync()
    {
        var pubId = await SeedAppIdentityPublishedGuideAsync();
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var published = await db.PublishedGuides
            .Include(pg => pg.Notebook)
            .SingleAsync(pg => pg.Id == pubId);

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Name = "App Identity Stream User",
            Email = $"app-identity-{userId:N}@example.com",
            PasswordHash = "integration-test-hash",
            SecurityStamp = Guid.NewGuid(),
            ApprovedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            Role = Role.Reader,
            AssignedAt = DateTime.UtcNow,
            AssignedByUserId = userId
        });
        await db.SaveChangesAsync();

        var conversation = new NotebookConversation
        {
            NotebookId = published.NotebookId,
            Title = "AppIdentity conversation"
        };
        db.NotebookConversations.Add(conversation);
        await db.SaveChangesAsync();

        return new SeededConversation(
            published.Notebook.ProjectId,
            published.NotebookId,
            conversation.Id,
            pubId,
            userId);
    }
}
