using FluentAssertions;
using GuideAntsApi.Services.Auth;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class SlidingSessionRenewalTests
{
    private static readonly DateTime Now = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void ShouldRenew_ReturnsFalse_ForFreshToken()
    {
        var issuedAt = Now - TimeSpan.FromHours(1);

        SlidingSessionRenewal.ShouldRenew(issuedAt, Now).Should().BeFalse();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsTrue_OnceRenewalIntervalElapsed()
    {
        var issuedAt = Now - SlidingSessionRenewal.RenewalInterval;

        SlidingSessionRenewal.ShouldRenew(issuedAt, Now).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsTrue_ForOlderToken()
    {
        var issuedAt = Now - (SlidingSessionRenewal.RenewalInterval + TimeSpan.FromDays(3));

        SlidingSessionRenewal.ShouldRenew(issuedAt, Now).Should().BeTrue();
    }

    [TestMethod]
    public void ShouldRenew_ReturnsFalse_ForUnsetIssuanceTime()
    {
        SlidingSessionRenewal.ShouldRenew(default, Now).Should().BeFalse();
    }
}
