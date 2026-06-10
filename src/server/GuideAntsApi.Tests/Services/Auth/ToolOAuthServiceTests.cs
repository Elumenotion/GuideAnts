using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Settings;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class ToolOAuthServiceTests
{
    [TestMethod]
    public async Task CreateAuthorizeUrlAsync_Throws_when_provider_id_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new ToolOAuthService(
            context,
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IOptionsMonitor<SettingsSecretsOptions>>(),
            NullLogger<ToolOAuthService>.Instance);

        var act = async () => await service.CreateAuthorizeUrlAsync(
            Guid.NewGuid(),
            providerId: " ",
            Guid.NewGuid(),
            new ToolOAuthAuthorizeUrlRequest(
                ClientId: "client",
                Tenant: "common",
                Scopes: ["openid"],
                RedirectUri: "https://localhost/callback",
                ReturnUrl: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*ProviderId is required*");
    }

    [TestMethod]
    public async Task CreateAuthorizeUrlAsync_Returns_authorize_url_for_valid_request()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-url-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new ToolOAuthService(
            context,
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IOptionsMonitor<SettingsSecretsOptions>>(),
            NullLogger<ToolOAuthService>.Instance);

        var result = await service.CreateAuthorizeUrlAsync(
            Guid.NewGuid(),
            providerId: "microsoft",
            Guid.NewGuid(),
            new ToolOAuthAuthorizeUrlRequest(
                ClientId: "client-id",
                Tenant: "common",
                Scopes: ["openid", "profile"],
                RedirectUri: "https://localhost/callback",
                ReturnUrl: null),
            CancellationToken.None);

        result.AuthorizeUrl.Should().StartWith("https://login.microsoftonline.com/");
        result.State.Should().NotBeNullOrWhiteSpace();
        (await context.OAuthAuthorizationStates.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task CreateAuthorizeUrlAsync_Throws_for_invalid_redirect_uri()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-redirect-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = new ToolOAuthService(
            context,
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IOptionsMonitor<SettingsSecretsOptions>>(),
            NullLogger<ToolOAuthService>.Instance);

        var act = async () => await service.CreateAuthorizeUrlAsync(
            Guid.NewGuid(),
            "microsoft",
            Guid.NewGuid(),
            new ToolOAuthAuthorizeUrlRequest(
                ClientId: "client-id",
                Tenant: "common",
                Scopes: ["openid"],
                RedirectUri: "not-a-url",
                ReturnUrl: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*RedirectUri*");
    }
}

