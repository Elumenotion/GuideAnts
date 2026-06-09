using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Services.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Endpoints;

[TestClass]
public sealed class AdminUsersEndpointsTests
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
    public async Task NonAdminToken_ReturnsForbidden_ForAllAdminUserRoutes()
    {
        var admin = await RegisterAsync("First Admin", "admin.one@example.com", "Password123!");
        var pendingUser = await RegisterAsync("Pending User", "pending.user@example.com", "Password123!");

        AuthCookieTestHelper.SetBearerToken(_client, pendingUser.Token);

        var listResponse = await _client.GetAsync("/api/admin/users");
        var approveResponse = await _client.PostAsJsonAsync($"/api/admin/users/{admin.UserId}/approve", new { role = Role.Reader.ToString() });
        var changeRoleResponse = await _client.PutAsJsonAsync($"/api/admin/users/{admin.UserId}/role", new { role = Role.Contributor.ToString() });
        var deactivateResponse = await _client.PostAsync($"/api/admin/users/{admin.UserId}/deactivate", content: null);
        var reactivateResponse = await _client.PostAsync($"/api/admin/users/{admin.UserId}/reactivate", content: null);
        var setPasswordResponse = await _client.PostAsJsonAsync($"/api/admin/users/{admin.UserId}/set-password", new { password = "AnotherPassword123!" });

        listResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        changeRoleResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        reactivateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        setPasswordResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task Approve_AssignsRoleAndApproverAndRejectsInvalidInput()
    {
        var admin = await RegisterAsync("Bootstrap Admin", "bootstrap.admin@example.com", "Password123!");
        var pendingUser = await RegisterAsync("Pending User", "needs.approval@example.com", "Password123!");
        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);

        var approveMissingResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{Guid.NewGuid()}/approve",
            new { role = Role.Reader.ToString() });
        approveMissingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var invalidRoleResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{pendingUser.UserId}/approve",
            new { role = "Owner" });
        invalidRoleResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{pendingUser.UserId}/approve",
            new { role = Role.Contributor.ToString() });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var approvedUser = await db.Users.SingleAsync(user => user.Id == pendingUser.UserId);
        var approvedRole = await db.UserRoles.SingleAsync(userRole => userRole.UserId == pendingUser.UserId);

        approvedRole.Role.Should().Be(Role.Contributor);
        approvedRole.AssignedByUserId.Should().Be(admin.UserId);
        approvedUser.ApprovedByUserId.Should().Be(admin.UserId);
        approvedUser.ApprovedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ApprovedUser_GainsApprovedAccessImmediately_WithoutReLogin()
    {
        var admin = await RegisterAsync("Bootstrap Admin", "immediate.admin@example.com", "Password123!");
        var pendingUser = await RegisterAsync("Pending User", "immediate.pending@example.com", "Password123!");

        // The pending user's existing session token, captured before approval (role = Pending).
        var preApprovalToken = pendingUser.Token;

        // While pending, an approved-only route must be forbidden (authenticated, but not authorized).
        AuthCookieTestHelper.SetBearerToken(_client, preApprovalToken);
        var beforeApproval = await _client.GetAsync("/api/catalogs/models");
        beforeApproval.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Admin approves the pending user as a Reader.
        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);
        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{pendingUser.UserId}/approve",
            new { role = Role.Reader.ToString() });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Using the SAME pre-approval token (no sign-out/sign-in), the route must now succeed
        // because RBAC resolves the live role per request rather than trusting the stale claim.
        AuthCookieTestHelper.SetBearerToken(_client, preApprovalToken);
        var afterApproval = await _client.GetAsync("/api/catalogs/models");
        afterApproval.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ChangeRole_DoesNotRevokeSession_NorForceReLogin()
    {
        var admin = await RegisterAsync("Bootstrap Admin", "rolechange.admin@example.com", "Password123!");
        var target = await RegisterAsync("Role Target", "rolechange.target@example.com", "Password123!");

        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);
        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{target.UserId}/approve",
            new { role = Role.Reader.ToString() });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Target establishes an active session as Reader.
        var readerSession = await LoginAsync(target.Email, "Password123!");
        var readerStamp = ReadSecurityStampClaim(readerSession.Token);

        AuthCookieTestHelper.SetBearerToken(_client, readerSession.Token);
        var beforeChange = await _client.GetAsync("/api/catalogs/models");
        beforeChange.StatusCode.Should().Be(HttpStatusCode.OK);

        // Admin promotes the target to Contributor.
        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);
        var changeRoleResponse = await _client.PutAsJsonAsync(
            $"/api/admin/users/{target.UserId}/role",
            new { role = Role.Contributor.ToString() });
        changeRoleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // A role change is an authority change, not a revocation: the security stamp must be
        // unchanged so the user's active session is not invalidated.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(candidate => candidate.Id == target.UserId);
            user.SecurityStamp.Should().Be(readerStamp);
        }

        // The same pre-change token still authenticates (no forced sign-out).
        AuthCookieTestHelper.SetBearerToken(_client, readerSession.Token);
        var afterChange = await _client.GetAsync("/api/catalogs/models");
        afterChange.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ChangeRole_UpdatesSingleRoleRow()
    {
        var admin = await RegisterAsync("Bootstrap Admin", "role.admin@example.com", "Password123!");
        var pendingUser = await RegisterAsync("Role Target", "role.target@example.com", "Password123!");
        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);

        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{pendingUser.UserId}/approve",
            new { role = Role.Reader.ToString() });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var roleChangeResponse = await _client.PutAsJsonAsync(
            $"/api/admin/users/{pendingUser.UserId}/role",
            new { role = Role.Admin.ToString() });
        roleChangeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var roleRowCount = await db.UserRoles.CountAsync(userRole => userRole.UserId == pendingUser.UserId);
        var roleRow = await db.UserRoles.SingleAsync(userRole => userRole.UserId == pendingUser.UserId);

        roleRowCount.Should().Be(1);
        roleRow.Role.Should().Be(Role.Admin);
    }

    [TestMethod]
    public async Task LastAdminSafeguard_BlocksDeactivateAndDemote()
    {
        var admin = await RegisterAsync("Solo Admin", "solo.admin@example.com", "Password123!");
        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);

        var deactivateResponse = await _client.PostAsync($"/api/admin/users/{admin.UserId}/deactivate", content: null);
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var demoteResponse = await _client.PutAsJsonAsync(
            $"/api/admin/users/{admin.UserId}/role",
            new { role = Role.Reader.ToString() });
        demoteResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var activeAdminCount = await db.UserRoles
            .Where(userRole => userRole.Role == Role.Admin && userRole.User.ApprovedAt != null)
            .CountAsync();
        activeAdminCount.Should().Be(1);
    }

    [TestMethod]
    public async Task SetPassword_UsesHasherAndInvalidatesPreviousToken()
    {
        const string oldPassword = "Password123!";
        const string newPassword = "Password456!";

        var admin = await RegisterAsync("Bootstrap Admin", "password.admin@example.com", oldPassword);
        var targetUser = await RegisterAsync("Password Target", "password.target@example.com", oldPassword);

        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);
        var approveResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{targetUser.UserId}/approve",
            new { role = Role.Contributor.ToString() });
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var oldLogin = await LoginAsync(targetUser.Email, oldPassword);
        var oldToken = oldLogin.Token;
        var oldTokenSecurityStamp = ReadSecurityStampClaim(oldToken);

        AuthCookieTestHelper.SetBearerToken(_client, admin.Token);
        var setPasswordResponse = await _client.PostAsJsonAsync(
            $"/api/admin/users/{targetUser.UserId}/set-password",
            new { password = newPassword });
        setPasswordResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AuthCookieTestHelper.SetBearerToken(_client, null);
        var oldPasswordLoginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = targetUser.Email,
            password = oldPassword
        });
        oldPasswordLoginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newLogin = await LoginAsync(targetUser.Email, newPassword);
        newLogin.MustChangePassword.Should().BeTrue();

        AuthCookieTestHelper.SetBearerToken(_client, oldToken);
        var meWithOldTokenResponse = await _client.GetAsync("/api/auth/me");
        meWithOldTokenResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(candidate => candidate.Id == targetUser.UserId);
        user.MustChangePassword.Should().BeTrue();
        user.SecurityStamp.Should().NotBe(oldTokenSecurityStamp);
    }

    private async Task<AuthSession> RegisterAsync(string name, string email, string password)
    {
        AuthCookieTestHelper.SetBearerToken(_client, null);

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name,
            email,
            password
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        var token = AuthCookieTestHelper.ReadAuthToken(response);
        token.Should().NotBeNullOrWhiteSpace();
        return new AuthSession(body!.UserId, body.Name, body.Email, body.Role, body.MustChangePassword, token!);
    }

    private async Task<AuthSession> LoginAsync(string email, string password)
    {
        AuthCookieTestHelper.SetBearerToken(_client, null);

        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        var token = AuthCookieTestHelper.ReadAuthToken(response);
        token.Should().NotBeNullOrWhiteSpace();
        return new AuthSession(body!.UserId, body.Name, body.Email, body.Role, body.MustChangePassword, token!);
    }

    private static Guid ReadSecurityStampClaim(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var claimValue = jwt.Claims.FirstOrDefault(claim => claim.Type == JwtClaimTypes.SecurityStamp)?.Value;
        Guid.TryParse(claimValue, out var securityStamp);
        return securityStamp;
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

    private sealed record AuthSession(
        Guid UserId,
        string Name,
        string Email,
        string Role,
        bool MustChangePassword,
        string Token);
}
