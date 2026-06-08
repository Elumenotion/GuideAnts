using System.Security.Claims;
using FluentAssertions;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class AuthRoleClaimsTests
{
    [TestMethod]
    public void ApplyLiveRole_ReplacesStaleRoleClaim_WithLiveRole()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim(ClaimTypes.Role, Role.Pending.ToString())
        }, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);

        AuthRoleClaims.ApplyLiveRole(identity, Role.Reader);

        identity.FindAll(ClaimTypes.Role).Should().ContainSingle()
            .Which.Value.Should().Be(Role.Reader.ToString());
        new ClaimsPrincipal(identity).IsInRole(Role.Reader.ToString()).Should().BeTrue();
        new ClaimsPrincipal(identity).IsInRole(Role.Pending.ToString()).Should().BeFalse();
    }

    [TestMethod]
    public void ApplyLiveRole_AddsRoleClaim_WhenNonePresent()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "Test User")
        }, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);

        AuthRoleClaims.ApplyLiveRole(identity, Role.Admin);

        identity.FindAll(ClaimTypes.Role).Should().ContainSingle()
            .Which.Value.Should().Be(Role.Admin.ToString());
    }

    [TestMethod]
    public void ApplyLiveRole_CollapsesMultipleStaleRoleClaims_ToLiveRole()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, Role.Pending.ToString()),
            new Claim(ClaimTypes.Role, Role.Reader.ToString())
        }, authenticationType: "Test", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);

        AuthRoleClaims.ApplyLiveRole(identity, Role.Contributor);

        identity.FindAll(ClaimTypes.Role).Should().ContainSingle()
            .Which.Value.Should().Be(Role.Contributor.ToString());
    }
}
