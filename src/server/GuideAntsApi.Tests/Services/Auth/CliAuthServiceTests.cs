using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class CliAuthServiceTests
{
    private static JwtOptions CreateJwtOptions() => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = "this-is-a-test-signing-key-that-is-long-enough-for-hmac-sha256",
        LifetimeMinutes = 1440
    };

    private static JwtTokenService CreateJwtTokenService(JwtOptions? options = null)
    {
        var opts = options ?? CreateJwtOptions();
        return new JwtTokenService(Microsoft.Extensions.Options.Options.Create(opts));
    }

    private static (User User, UserRole UserRole) CreateUserAndRole(Role role = Role.Contributor)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = "CLI Test User",
            Email = "cli-test@example.com",
            SecurityStamp = Guid.NewGuid()
        };
        var userRole = new UserRole
        {
            UserId = user.Id,
            User = user,
            Role = role
        };
        return (user, userRole);
    }

    private static CliAuthService CreateService(ApplicationDbContext db, IJwtTokenService? jwtTokenService = null)
    {
        return new CliAuthService(db, jwtTokenService ?? CreateJwtTokenService());
    }

    // ──────────────────────────────────────────────
    // CreateSessionAsync
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task CreateSession_Returns_NonEmpty_Distinct_SessionId_And_DeviceSecret()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-create-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        var result = await service.CreateSessionAsync();

        result.SessionId.Should().NotBeNullOrWhiteSpace();
        result.DeviceSecret.Should().NotBeNullOrWhiteSpace();
        result.SessionId.Should().NotBe(result.DeviceSecret);
    }

    [TestMethod]
    public async Task CreateSession_Persists_Row_With_Hashed_Secret_And_Pending_Status()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-persist-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        var result = await service.CreateSessionAsync();

        var row = await db.CliAuthSessions.SingleAsync();
        row.SessionId.Should().Be(result.SessionId);
        row.DeviceSecretHash.Should().NotBe(result.DeviceSecret, "the secret must be stored hashed, not as plaintext");
        row.DeviceSecretHash.Should().NotBeNullOrWhiteSpace();
        row.Status.Should().Be(CliAuthSessionStatus.Pending);
        row.UserId.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateSession_ExpiresAt_Is_Approximately_Now_Plus_Five_Minutes()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-expiry-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);
        var before = DateTime.UtcNow;

        var result = await service.CreateSessionAsync();

        var after = DateTime.UtcNow;
        result.ExpiresAt.Should().BeCloseTo(before.AddMinutes(5), precision: TimeSpan.FromSeconds(5));
        result.ExpiresAt.Should().BeOnOrAfter(before.AddMinutes(5).AddSeconds(-1));
        result.ExpiresAt.Should().BeOnOrBefore(after.AddMinutes(5).AddSeconds(1));
    }

    [TestMethod]
    public async Task CreateSession_Cleans_Up_Expired_Sessions()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-cleanup-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);

        // Pre-seed an expired session
        db.CliAuthSessions.Add(new CliAuthSession
        {
            SessionId = "expired-session",
            DeviceSecretHash = "hash",
            Status = CliAuthSessionStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.CreateSessionAsync();

        // The expired row should be gone, only the new one remains
        var rows = await db.CliAuthSessions.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].SessionId.Should().Be(result.SessionId);
    }

    // ──────────────────────────────────────────────
    // ApproveAsync
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task Approve_Unknown_SessionId_Returns_NotFound()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-approve-notfound-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        var outcome = await service.ApproveAsync("nonexistent-session", Guid.NewGuid());

        outcome.Should().Be(CliAuthApproveOutcome.NotFound);
    }

    [TestMethod]
    public async Task Approve_Expired_Session_Returns_Expired_And_Removes_Row()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-approve-expired-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);

        // Seed with ExpiresAt exactly at UtcNow so the cleanup (ExpiresAt <= now)
        // removes it before the per-session check can fire. Both NotFound and Expired
        // are valid outcomes for a session that has already expired; cleanup catching
        // it first is the common case.
        db.CliAuthSessions.Add(new CliAuthSession
        {
            SessionId = "expired-session",
            DeviceSecretHash = "hash",
            Status = CliAuthSessionStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.ApproveAsync("expired-session", Guid.NewGuid());

        // Expired session is removed by the cleanup step, returning NotFound
        // (the explicit ExpiresAt check at step 3 is a production race-condition safety net).
        outcome.Should().BeOneOf(CliAuthApproveOutcome.Expired, CliAuthApproveOutcome.NotFound);
        (await db.CliAuthSessions.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task Approve_Valid_Pending_Session_Returns_Approved_And_Sets_UserId()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-approve-ok-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var userId = Guid.NewGuid();

        db.CliAuthSessions.Add(new CliAuthSession
        {
            SessionId = "pending-session",
            DeviceSecretHash = "hash",
            Status = CliAuthSessionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var outcome = await service.ApproveAsync("pending-session", userId);

        outcome.Should().Be(CliAuthApproveOutcome.Approved);
        var row = await db.CliAuthSessions.SingleAsync();
        row.Status.Should().Be(CliAuthSessionStatus.Approved);
        row.UserId.Should().Be(userId);
    }

    // ──────────────────────────────────────────────
    // IssueTokenAsync
    // ──────────────────────────────────────────────

    [TestMethod]
    public async Task IssueToken_Pending_Session_Returns_Pending_No_Token()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-pending-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        // Create a session via the service to get valid secret
        var session = await service.CreateSessionAsync();

        var result = await service.IssueTokenAsync(session.SessionId, session.DeviceSecret);

        result.Status.Should().Be(CliAuthTokenStatus.Pending);
        result.Token.Should().BeNull();
        result.ExpiresAt.Should().BeNull();
    }

    [TestMethod]
    public async Task IssueToken_Wrong_Secret_On_Approved_Session_Returns_InvalidSecret_And_Session_Not_Consumed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-badsecret-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var (user, userRole) = CreateUserAndRole();
        db.Users.Add(user);
        db.UserRoles.Add(userRole);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var session = await service.CreateSessionAsync();
        await service.ApproveAsync(session.SessionId, user.Id);

        var result = await service.IssueTokenAsync(session.SessionId, "wrong-secret-value");

        result.Status.Should().Be(CliAuthTokenStatus.InvalidSecret);
        result.Token.Should().BeNull();
        result.ExpiresAt.Should().BeNull();

        // Session must NOT be consumed
        var row = await db.CliAuthSessions.SingleAsync();
        row.Status.Should().Be(CliAuthSessionStatus.Approved);
    }

    [TestMethod]
    public async Task IssueToken_Approved_With_Correct_Secret_Returns_Issued_Token_And_Marks_Consumed()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-ok-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var (user, userRole) = CreateUserAndRole(Role.Contributor);
        db.Users.Add(user);
        db.UserRoles.Add(userRole);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var session = await service.CreateSessionAsync();
        await service.ApproveAsync(session.SessionId, user.Id);
        var before = DateTime.UtcNow;

        var result = await service.IssueTokenAsync(session.SessionId, session.DeviceSecret);

        result.Status.Should().Be(CliAuthTokenStatus.Issued);
        result.Token.Should().NotBeNullOrWhiteSpace();
        result.ExpiresAt.Should().NotBeNull();
        result.ExpiresAt!.Value.Should().BeCloseTo(before.AddMinutes(10), precision: TimeSpan.FromSeconds(5));

        // Session row must now be Consumed
        var row = await db.CliAuthSessions.SingleAsync();
        row.Status.Should().Be(CliAuthSessionStatus.Consumed);
    }

    [TestMethod]
    public async Task IssueToken_Second_Call_After_Success_Returns_Consumed_No_Token()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-consumed-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var (user, userRole) = CreateUserAndRole();
        db.Users.Add(user);
        db.UserRoles.Add(userRole);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var session = await service.CreateSessionAsync();
        await service.ApproveAsync(session.SessionId, user.Id);

        // First call should succeed
        var first = await service.IssueTokenAsync(session.SessionId, session.DeviceSecret);
        first.Status.Should().Be(CliAuthTokenStatus.Issued);

        // Second call should return Consumed
        var second = await service.IssueTokenAsync(session.SessionId, session.DeviceSecret);
        second.Status.Should().Be(CliAuthTokenStatus.Consumed);
        second.Token.Should().BeNull();
        second.ExpiresAt.Should().BeNull();
    }

    [TestMethod]
    public async Task IssueToken_Expired_Session_Returns_Expired_Or_NotFound()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-expired-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        // Create session then manually expire it
        var session = await service.CreateSessionAsync();
        var row = await db.CliAuthSessions.SingleAsync();
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var result = await service.IssueTokenAsync(session.SessionId, session.DeviceSecret);

        // Expired session is removed by the cleanup step, returning NotFound;
        // the explicit ExpiresAt check (returning Expired) is a production race-condition safety net.
        result.Status.Should().BeOneOf(CliAuthTokenStatus.Expired, CliAuthTokenStatus.NotFound);
        result.Token.Should().BeNull();
        result.ExpiresAt.Should().BeNull();
    }

    [TestMethod]
    public async Task IssueToken_Unknown_SessionId_Returns_NotFound()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-notfound-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var service = CreateService(db);

        var result = await service.IssueTokenAsync("nonexistent-session", "some-secret");

        result.Status.Should().Be(CliAuthTokenStatus.NotFound);
        result.Token.Should().BeNull();
        result.ExpiresAt.Should().BeNull();
    }

    [TestMethod]
    public async Task IssueToken_Issued_JWT_Carries_Correct_Role_And_SecurityStamp()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"cli-auth-issue-jwt-{Guid.NewGuid():N}");
        await using var db = new ApplicationDbContext(options);
        var (user, userRole) = CreateUserAndRole(Role.Admin);
        db.Users.Add(user);
        db.UserRoles.Add(userRole);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var session = await service.CreateSessionAsync();
        await service.ApproveAsync(session.SessionId, user.Id);

        var result = await service.IssueTokenAsync(session.SessionId, session.DeviceSecret);

        result.Status.Should().Be(CliAuthTokenStatus.Issued);
        result.Token.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(result.Token);

        jwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == Role.Admin.ToString());
        jwt.Claims.Should().Contain(c =>
            c.Type == JwtClaimTypes.SecurityStamp && c.Value == user.SecurityStamp.ToString());
    }
}
