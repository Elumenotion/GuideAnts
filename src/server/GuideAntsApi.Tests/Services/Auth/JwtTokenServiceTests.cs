using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Auth;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class JwtTokenServiceTests
{
    private static JwtOptions CreateOptions(int lifetimeMinutes = 1440) => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = "this-is-a-test-signing-key-that-is-long-enough-for-hmac-sha256",
        LifetimeMinutes = lifetimeMinutes
    };

    private static User CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test User",
        Email = "test@example.com",
        SecurityStamp = Guid.NewGuid()
    };

    private static JwtTokenService CreateService(JwtOptions? options = null)
    {
        var opts = options ?? CreateOptions();
        return new JwtTokenService(Microsoft.Extensions.Options.Options.Create(opts));
    }

    [TestMethod]
    public void IssueToken_WithLifetimeOverride_UsesOverrideExpiry()
    {
        var service = CreateService();
        var user = CreateUser();
        var overrideDuration = TimeSpan.FromMinutes(10);
        var beforeUtc = DateTime.UtcNow;

        var result = service.IssueToken(user, Role.Reader, overrideDuration);

        var afterUtc = DateTime.UtcNow;
        // ExpiresAtUtc should be ~10 minutes from now, not the default 1440
        result.ExpiresAtUtc.Should().BeCloseTo(beforeUtc + overrideDuration, precision: TimeSpan.FromSeconds(5));
        result.ExpiresAtUtc.Should().BeBefore(afterUtc.AddMinutes(11));
        // Definitely not near the default 1440 minutes
        result.ExpiresAtUtc.Should().BeBefore(DateTime.UtcNow.AddMinutes(20));
    }

    [TestMethod]
    public void IssueToken_WithoutLifetimeOverride_UsesDefaultExpiry()
    {
        var options = CreateOptions(lifetimeMinutes: 1440);
        var service = CreateService(options);
        var user = CreateUser();
        var beforeUtc = DateTime.UtcNow;

        var result = service.IssueToken(user, Role.Reader);

        result.ExpiresAtUtc.Should().BeCloseTo(
            beforeUtc.AddMinutes(1440), precision: TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void IssueToken_WithLifetimeOverride_HasSameClaimsAsDefault()
    {
        var service = CreateService();
        var user = CreateUser();
        var handler = new JwtSecurityTokenHandler();

        var defaultResult = service.IssueToken(user, Role.Contributor);
        var overrideResult = service.IssueToken(user, Role.Contributor, TimeSpan.FromMinutes(10));

        var defaultToken = handler.ReadJwtToken(defaultResult.Token);
        var overrideToken = handler.ReadJwtToken(overrideResult.Token);

        // Role claim present and matching
        overrideToken.Claims.Should().Contain(c =>
            c.Type == ClaimTypes.Role && c.Value == Role.Contributor.ToString());

        // SecurityStamp claim present and matching
        var defaultStamp = defaultToken.Claims.First(c => c.Type == JwtClaimTypes.SecurityStamp).Value;
        var overrideStamp = overrideToken.Claims.First(c => c.Type == JwtClaimTypes.SecurityStamp).Value;
        overrideStamp.Should().Be(defaultStamp);

        // Sub claim present and matching
        var defaultSub = defaultToken.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value;
        var overrideSub = overrideToken.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value;
        overrideSub.Should().Be(defaultSub);
    }

    [TestMethod]
    public void IssueToken_WithZeroLifetimeOverride_ThrowsArgumentOutOfRangeException()
    {
        var service = CreateService();
        var user = CreateUser();

        var act = () => service.IssueToken(user, Role.Reader, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("lifetimeOverride");
    }

    [TestMethod]
    public void IssueToken_WithNegativeLifetimeOverride_ThrowsArgumentOutOfRangeException()
    {
        var service = CreateService();
        var user = CreateUser();

        var act = () => service.IssueToken(user, Role.Reader, TimeSpan.FromMinutes(-5));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("lifetimeOverride");
    }
}
