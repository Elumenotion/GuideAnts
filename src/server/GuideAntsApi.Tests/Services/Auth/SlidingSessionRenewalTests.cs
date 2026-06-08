using FluentAssertions;
using GuideAntsApi.Services.Auth;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class SlidingSessionRenewalTests
{
    private static readonly DateTime Now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    [TestMethod]
    public void ShouldRenew_ReturnsFalse_ForFreshToken()
    {
        var issuedAt = Now - TimeSpan.FromHours(1);
        var expiresAt = issuedAt + Lifetime;

        SlidingSessionRenewal.ShouldRenew(issuedAt, expiresAt, Now).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsTrue_OncePastHalfLifetime()
    {
        var issuedAt = Now - TimeSpan.FromTicks((long)(Lifetime.Ticks * SlidingSessionRenewal.RenewAfterLifetimeFraction));
        var expiresAt = issuedAt + Lifetime;

        SlidingSessionRenewal.ShouldRenew(issuedAt, expiresAt, Now).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsTrue_ForOlderToken()
    {
        var issuedAt = Now - (Lifetime - TimeSpan.FromMinutes(1));
        var expiresAt = issuedAt + Lifetime;

        SlidingSessionRenewal.ShouldRenew(issuedAt, expiresAt, Now).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldRenew_RenewsWellBeforeExpiry_ForShortLifetime()
    {
        // A short lifetime must still renew before it expires — a fixed interval longer than
        // the lifetime would never fire and hard-expire the session.
        var shortLifetime = TimeSpan.FromMinutes(60);
        var issuedAt = Now - TimeSpan.FromMinutes(31);
        var expiresAt = issuedAt + shortLifetime;

        SlidingSessionRenewal.ShouldRenew(issuedAt, expiresAt, Now).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsFalse_ForUnsetIssuanceTime()
    {
        SlidingSessionRenewal.ShouldRenew(default, default, Now).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsFalse_ForNonPositiveLifetime()
    {
        var issuedAt = Now - TimeSpan.FromDays(1);

        SlidingSessionRenewal.ShouldRenew(issuedAt, issuedAt, Now).Should().BeFalse();
    }
}
