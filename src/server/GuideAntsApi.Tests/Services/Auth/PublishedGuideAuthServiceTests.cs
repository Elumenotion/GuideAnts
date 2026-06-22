using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class PublishedGuideAuthServiceTests
{
    private static readonly JwtOptions TestJwtOptions = new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = "test-signing-key-must-be-at-least-32-chars",
        LifetimeMinutes = 60
    };

    [TestMethod]
    public async Task ValidateAsync_Returns_invalid_when_published_guide_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-auth-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp => sp.GetRequiredService<TestDbContextFactory>());
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var service = CreateService(options);

        var result = await service.ValidateAsync(
            Guid.NewGuid(),
            authorizationHeader: null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_published_guide");
    }

    [TestMethod]
    public async Task ValidateAsync_Allows_anonymous_when_auth_mode_anonymous()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-anon-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthMode.Anonymous);
        var service = CreateService(options);

        var result = await service.ValidateAsync(pubId, authorizationHeader: null, Guid.NewGuid(), notebookId, CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.UserIdentity.Should().BeNull();
        result.InternalUserId.Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAsync_Requires_api_key_when_auth_mode_api_key()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-key-req-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(
            options,
            pubId,
            PublishedGuideAuthMode.ApiKey,
            apiKeyHash: PublishedGuideAuthService.HashApiKey("gak_test"));
        var service = CreateService(options);

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: null,
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None,
            apiKeyHeader: null);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("api_key_required");
    }

    [TestMethod]
    public async Task ValidateAsync_Rejects_invalid_api_key()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-bad-key-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var validKey = PublishedGuideAuthService.GenerateApiKey();
        var (_, notebookId) = await SeedPublishedGuideAsync(
            options,
            pubId,
            PublishedGuideAuthMode.ApiKey,
            PublishedGuideAuthService.HashApiKey(validKey));
        var service = CreateService(options);

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: null,
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None,
            apiKeyHeader: "gak_wrong");

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_api_key");
    }

    [TestMethod]
    public async Task ValidateAsync_Accepts_valid_api_key()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-good-key-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var apiKey = PublishedGuideAuthService.GenerateApiKey();
        var (_, notebookId) = await SeedPublishedGuideAsync(
            options,
            pubId,
            PublishedGuideAuthMode.ApiKey,
            PublishedGuideAuthService.HashApiKey(apiKey));
        var service = CreateService(options);

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: null,
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None,
            apiKeyHeader: apiKey);

        result.IsValid.Should().BeTrue();
        result.UserIdentity.Should().Be("api-key-user");
        result.InternalUserId.Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAsync_Requires_auth_header_when_auth_mode_webhook()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-webhook-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(
            options,
            pubId,
            PublishedGuideAuthMode.Webhook,
            webhookUrl: "https://auth.example.com/validate");
        var service = CreateService(options);

        var result = await service.ValidateAsync(pubId, authorizationHeader: null, Guid.NewGuid(), notebookId, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("authentication_required");
    }

    [TestMethod]
    public async Task ValidateAsync_AppIdentity_missing_token_returns_authentication_required()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-app-missing-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthMode.AppIdentity);
        var service = CreateService(options);

        var result = await service.ValidateAsync(pubId, authorizationHeader: null, Guid.NewGuid(), notebookId, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("authentication_required");
        result.InternalUserId.Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAsync_AppIdentity_expired_token_returns_invalid_token()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-app-expired-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (userId, user) = await SeedApprovedUserAsync(options);
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthMode.AppIdentity);
        var service = CreateService(options);
        var expiredToken = CreateExpiredToken(user);

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: $"Bearer {expiredToken}",
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_token");
    }

    [TestMethod]
    public async Task ValidateAsync_AppIdentity_valid_cookie_token_sets_internal_user_id()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-app-valid-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (userId, user) = await SeedApprovedUserAsync(options);
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthMode.AppIdentity);
        var service = CreateService(options);
        var jwtService = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(TestJwtOptions));
        var token = jwtService.IssueToken(user, Role.Reader).Token;

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: null,
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None,
            appAuthCookieToken: token);

        result.IsValid.Should().BeTrue();
        result.UserIdentity.Should().Be(userId.ToString());
        result.InternalUserId.Should().Be(userId);
    }

    [TestMethod]
    public async Task ValidateAsync_AppIdentity_valid_bearer_header_sets_internal_user_id()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-app-bearer-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (userId, user) = await SeedApprovedUserAsync(options);
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthMode.AppIdentity);
        var service = CreateService(options);
        var jwtService = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(TestJwtOptions));
        var token = jwtService.IssueToken(user, Role.Contributor).Token;

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: $"Bearer {token}",
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.InternalUserId.Should().Be(userId);
    }

    [TestMethod]
    public async Task ValidateAsync_AppIdentity_pending_user_returns_user_not_approved()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-app-pending-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, user) = await SeedApprovedUserAsync(options, Role.Pending, approved: false);
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthMode.AppIdentity);
        var service = CreateService(options);
        var jwtService = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(TestJwtOptions));
        var token = jwtService.IssueToken(user, Role.Pending).Token;

        var result = await service.ValidateAsync(
            pubId,
            authorizationHeader: $"Bearer {token}",
            Guid.NewGuid(),
            notebookId,
            CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("user_not_approved");
    }

    [TestMethod]
    public void HashApiKey_Is_deterministic_for_same_input()
    {
        const string key = "gak_testkey123";
        PublishedGuideAuthService.HashApiKey(key).Should().Be(PublishedGuideAuthService.HashApiKey(key));
    }

    [TestMethod]
    public void GenerateApiKey_Produces_prefixed_unique_values()
    {
        var key1 = PublishedGuideAuthService.GenerateApiKey();
        var key2 = PublishedGuideAuthService.GenerateApiKey();

        key1.Should().StartWith("gak_");
        key2.Should().StartWith("gak_");
        key1.Should().NotBe(key2);
    }

    private static PublishedGuideAuthService CreateService(DbContextOptions<ApplicationDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp => sp.GetRequiredService<TestDbContextFactory>());
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();

        return new PublishedGuideAuthService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IHttpClientFactory>(),
            new AppJwtValidator(Microsoft.Extensions.Options.Options.Create(TestJwtOptions)),
            NullLogger<PublishedGuideAuthService>.Instance);
    }

    private static async Task<(Guid ProjectId, Guid NotebookId)> SeedPublishedGuideAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid pubId,
        PublishedGuideAuthMode authMode,
        string? apiKeyHash = null,
        string? webhookUrl = null)
    {
        await using var context = new ApplicationDbContext(options);
        var (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(context);
        context.PublishedGuides.Add(new PublishedGuide
        {
            Id = pubId,
            GuideId = Guid.NewGuid(),
            NotebookId = notebookId,
            Active = true,
            AuthMode = authMode,
            ApiKeyHash = apiKeyHash,
            AuthValidationWebhookUrl = webhookUrl
        });
        await context.SaveChangesAsync();
        return (projectId, notebookId);
    }

    private static async Task<(Guid UserId, User User)> SeedApprovedUserAsync(
        DbContextOptions<ApplicationDbContext> options,
        Role role = Role.Reader,
        bool approved = true)
    {
        await using var context = new ApplicationDbContext(options);
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Name = "Auth Test User",
            Email = $"auth-test-{userId:N}@example.com",
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid(),
            ApprovedAt = approved && role != Role.Pending ? DateTime.UtcNow : null
        };
        context.Users.Add(user);
        context.UserRoles.Add(new UserRole
        {
            UserId = userId,
            Role = role,
            AssignedAt = DateTime.UtcNow,
            AssignedByUserId = userId
        });
        await context.SaveChangesAsync();
        return (userId, user);
    }

    private static string CreateExpiredToken(User user)
    {
        var nowUtc = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, Role.Reader.ToString()),
            new(JwtClaimTypes.SecurityStamp, user.SecurityStamp.ToString())
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtOptions.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: TestJwtOptions.Issuer,
            audience: TestJwtOptions.Audience,
            claims: claims,
            notBefore: nowUtc.AddHours(-2),
            expires: nowUtc.AddHours(-1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
