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
public class CliAuthEndpointsTests
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
    public async Task CreateSession_Anonymous_Returns200WithSessionFields()
    {
        var response = await _client.PostAsync("/api/cli/sessions", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateSessionResponse>();
        body.Should().NotBeNull();
        body!.SessionId.Should().NotBeNullOrWhiteSpace();
        body.DeviceSecret.Should().NotBeNullOrWhiteSpace();
        body.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [TestMethod]
    public async Task GetToken_BeforeApproval_Returns202Pending()
    {
        // Create session
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();

        // Poll token before approval
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/cli/sessions/{session!.SessionId}/token");
        request.Headers.Add("X-Device-Secret", session.DeviceSecret);

        var tokenResponse = await _client.SendAsync(request);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.Accepted); // 202
    }

    [TestMethod]
    public async Task GetToken_WrongDeviceSecret_Returns401()
    {
        // Create session
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();

        // Poll with wrong secret
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/cli/sessions/{session!.SessionId}/token");
        request.Headers.Add("X-Device-Secret", "wrong-secret-value");

        var tokenResponse = await _client.SendAsync(request);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task GetToken_NoDeviceSecretHeader_Returns401()
    {
        // Create session
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();

        // Poll without X-Device-Secret header
        var tokenResponse = await _client.GetAsync(
            $"/api/cli/sessions/{session!.SessionId}/token");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task FullFlow_CreateApproveIssue_TokenAuthenticatesAgainstMe()
    {
        // 1. Create session (anonymous)
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();
        session.Should().NotBeNull();

        // 2. Approve as authenticated admin user
        var adminToken = IntegrationTestAuthTokenFactory.CreateAdminToken();
        using var approveClient = _factory.CreateClient();
        AuthCookieTestHelper.SetBearerToken(approveClient, adminToken);

        var approveResponse = await approveClient.PostAsync(
            $"/api/cli/sessions/{session!.SessionId}/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 3. Exchange device secret for JWT
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/cli/sessions/{session.SessionId}/token");
        tokenRequest.Headers.Add("X-Device-Secret", session.DeviceSecret);

        var tokenResponse = await _client.SendAsync(tokenRequest);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
        tokenBody.Should().NotBeNull();
        tokenBody!.Token.Should().NotBeNullOrWhiteSpace();
        tokenBody.ExpiresAt.Should().BeAfter(DateTime.UtcNow);

        // 4. Verify the issued JWT authenticates against GET /api/auth/me
        using var meClient = _factory.CreateClient();
        AuthCookieTestHelper.SetBearerToken(meClient, tokenBody.Token);

        var meResponse = await meClient.GetAsync("/api/auth/me");
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task GetToken_AfterConsumed_Returns410()
    {
        // Create + approve + issue (first call)
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();

        var adminToken = IntegrationTestAuthTokenFactory.CreateAdminToken();
        using var approveClient = _factory.CreateClient();
        AuthCookieTestHelper.SetBearerToken(approveClient, adminToken);
        var approveResponse = await approveClient.PostAsync(
            $"/api/cli/sessions/{session!.SessionId}/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // First token exchange — should succeed
        using var firstTokenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/cli/sessions/{session.SessionId}/token");
        firstTokenRequest.Headers.Add("X-Device-Secret", session.DeviceSecret);
        var firstTokenResponse = await _client.SendAsync(firstTokenRequest);
        firstTokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second token exchange — should return 410 Consumed
        using var secondTokenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/cli/sessions/{session.SessionId}/token");
        secondTokenRequest.Headers.Add("X-Device-Secret", session.DeviceSecret);
        var secondTokenResponse = await _client.SendAsync(secondTokenRequest);
        secondTokenResponse.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [TestMethod]
    public async Task Approve_WithoutAuthentication_Returns401()
    {
        // Create session
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();

        // Try to approve without authentication
        using var anonClient = _factory.CreateClient();
        var approveResponse = await anonClient.PostAsync(
            $"/api/cli/sessions/{session!.SessionId}/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Approve_AsNonAdmin_Returns403()
    {
        // Create session
        var createResponse = await _client.PostAsync("/api/cli/sessions", null);
        var session = await createResponse.Content.ReadFromJsonAsync<CreateSessionResponse>();

        // A non-admin (Reader) cannot complete a mount, so approval is admin-only.
        var readerToken = IntegrationTestAuthTokenFactory.CreateToken(Role.Reader);
        using var readerClient = _factory.CreateClient();
        AuthCookieTestHelper.SetBearerToken(readerClient, readerToken);
        var approveResponse = await readerClient.PostAsync(
            $"/api/cli/sessions/{session!.SessionId}/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task GetToken_UnknownSessionId_Returns404()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/cli/sessions/nonexistent-session-id/token");
        request.Headers.Add("X-Device-Secret", "any-secret");

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Login_StillReturns200_AfterTokenWriteRemoved()
    {
        // Register a user first (first user becomes admin)
        AuthCookieTestHelper.SetBearerToken(_client, null);
        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "CLI Test Admin",
            email = "cli.test.admin@example.com",
            password = "Password123!"
        });
        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Login should still succeed without the .cli-auth-token file write
        AuthCookieTestHelper.SetBearerToken(_client, null);
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "cli.test.admin@example.com",
            password = "Password123!"
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task ResetAuthStateAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Database.ExecuteSqlRawAsync("DELETE FROM [CliAuthSessions];");
        await db.Database.ExecuteSqlRawAsync("UPDATE [NotebookConversationMessages] SET [UserId] = NULL, [LastEditedByUserId] = NULL;");
        await db.Database.ExecuteSqlRawAsync("UPDATE [MessageEditHistories] SET [FirstEditedByUserId] = NULL;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [UserProjectContextOption];");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [UserRoles];");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [Users];");
    }

    private sealed record CreateSessionResponse(string SessionId, string DeviceSecret, DateTime ExpiresAt);

    private sealed record TokenResponse(string Token, DateTime ExpiresAt);
}
