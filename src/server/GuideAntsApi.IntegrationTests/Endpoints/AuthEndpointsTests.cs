using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public class AuthEndpointsTests
{
    private static WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        var baseFactory = new TestWebApplicationFactory();
        await baseFactory.InitializeAsync();
        _factory = baseFactory;
    }

    [TestInitialize]
    public async Task TestInitializeAsync()
    {
        _client = _factory.CreateClient();
        await ResetAuthStateAsync();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client.Dispose();
    }

    [TestMethod]
    public async Task RegisterLoginAndMeFlow_MatchesBackendAuthRequirements()
    {
        AuthCookieTestHelper.SetBearerToken(_client, null);

        var firstRegistration = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "First User",
            email = "first.user@example.com",
            password = "Password123!"
        });
        firstRegistration.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstAuth = await firstRegistration.Content.ReadFromJsonAsync<AuthResponse>();
        firstAuth.Should().NotBeNull();
        firstAuth!.Role.Should().Be(Role.Admin.ToString());

        var firstMeResponse = await _client.GetAsync("/api/auth/me");
        firstMeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstMe = await firstMeResponse.Content.ReadFromJsonAsync<AuthMeResponse>();
        firstMe.Should().NotBeNull();
        firstMe!.Role.Should().Be(Role.Admin.ToString());

        AuthCookieTestHelper.SetBearerToken(_client, null);
        var secondRegistration = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Second User",
            email = "second.user@example.com",
            password = "Password123!"
        });
        secondRegistration.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondAuth = await secondRegistration.Content.ReadFromJsonAsync<AuthResponse>();
        secondAuth.Should().NotBeNull();
        secondAuth!.Role.Should().Be(Role.Pending.ToString());

        var secondMeResponse = await _client.GetAsync("/api/auth/me");
        secondMeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondMe = await secondMeResponse.Content.ReadFromJsonAsync<AuthMeResponse>();
        secondMe.Should().NotBeNull();
        secondMe!.Role.Should().Be(Role.Pending.ToString());

        AuthCookieTestHelper.SetBearerToken(_client, null);
        var badLoginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "second.user@example.com",
            password = "WrongPassword!"
        });
        badLoginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var anonymousClient = _factory.CreateClient();
        var noAuthResponse = await anonymousClient.GetAsync("/api/auth/me");
        noAuthResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task RegisterPendingUserThenAdminApprove_AssignsRoleAndUnlocksApprovedEndpoints()
    {
        AuthCookieTestHelper.SetBearerToken(_client, null);

        var adminRegistration = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Bootstrap Admin",
            email = "bootstrap.auth@example.com",
            password = "Password123!"
        });
        adminRegistration.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminAuth = await adminRegistration.Content.ReadFromJsonAsync<AuthResponse>();
        adminAuth.Should().NotBeNull();
        adminAuth!.Role.Should().Be(Role.Admin.ToString());
        var adminToken = AuthCookieTestHelper.ReadAuthToken(adminRegistration);

        using var pendingClient = _factory.CreateClient();
        var pendingRegistration = await pendingClient.PostAsJsonAsync("/api/auth/register", new
        {
            name = "Pending Approval",
            email = "pending.approval@example.com",
            password = "Password123!"
        });
        pendingRegistration.StatusCode.Should().Be(HttpStatusCode.OK);
        var pendingAuth = await pendingRegistration.Content.ReadFromJsonAsync<AuthResponse>();
        pendingAuth.Should().NotBeNull();
        pendingAuth!.Role.Should().Be(Role.Pending.ToString());
        var pendingToken = AuthCookieTestHelper.ReadAuthToken(pendingRegistration);

        AuthCookieTestHelper.SetBearerToken(pendingClient, pendingToken);
        var pendingCurrentResponse = await pendingClient.GetAsync("/api/users/current");
        pendingCurrentResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        AuthCookieTestHelper.SetBearerToken(_client, adminToken);
        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{pendingAuth.UserId}/approve",
            new { role = Role.Reader.ToString() });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AuthCookieTestHelper.SetBearerToken(pendingClient, null);
        var approvedLoginResponse = await pendingClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = "pending.approval@example.com",
            password = "Password123!"
        });
        approvedLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var approvedAuth = await approvedLoginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        approvedAuth.Should().NotBeNull();
        approvedAuth!.Role.Should().Be(Role.Reader.ToString());

        var approvedCurrentResponse = await pendingClient.GetAsync("/api/users/current");
        approvedCurrentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ConcurrentInitialRegistrations_CreateExactlyOneAdmin()
    {
        AuthCookieTestHelper.SetBearerToken(_client, null);

        var firstClient = _factory.CreateClient();
        var secondClient = _factory.CreateClient();

        try
        {
            var registerOneTask = firstClient.PostAsJsonAsync("/api/auth/register", new
            {
                name = "Concurrent One",
                email = "concurrent.one@example.com",
                password = "Password123!"
            });
            var registerTwoTask = secondClient.PostAsJsonAsync("/api/auth/register", new
            {
                name = "Concurrent Two",
                email = "concurrent.two@example.com",
                password = "Password123!"
            });

            var responses = await Task.WhenAll(registerOneTask, registerTwoTask);
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        }
        finally
        {
            firstClient.Dispose();
            secondClient.Dispose();
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var adminCount = await db.UserRoles.CountAsync(userRole => userRole.Role == Role.Admin);
        var pendingCount = await db.UserRoles.CountAsync(userRole => userRole.Role == Role.Pending);

        adminCount.Should().Be(1);
        pendingCount.Should().Be(1);
    }

    private static async Task ResetAuthStateAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Database.ExecuteSqlRawAsync("UPDATE [NotebookConversationMessages] SET [UserId] = NULL, [LastEditedByUserId] = NULL;");
        await db.Database.ExecuteSqlRawAsync("UPDATE [MessageEditHistories] SET [FirstEditedByUserId] = NULL;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [UserProjectContextOption];");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [UserRoles];");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Users];");
    }

    private sealed record AuthResponse(
        Guid UserId,
        string Name,
        string Email,
        string Role,
        bool MustChangePassword);

    private sealed record AuthMeResponse(
        Guid UserId,
        string Name,
        string Email,
        string Role,
        bool MustChangePassword,
        DateTime? LastLoginAt);
}
