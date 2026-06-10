using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
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
        var service = CreateService(context);

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
        var service = CreateService(context);

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
    public async Task CreateAuthorizeUrlAsync_Uses_project_override_and_normalizes_return_url()
    {
        var dbOptions = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-override-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(dbOptions);
        var service = CreateService(context);
        var projectId = Guid.NewGuid();

        context.ProjectExternalAuths.Add(new ProjectExternalAuth
        {
            ProjectId = projectId,
            ProviderId = "microsoft",
            AuthType = "oauth",
            ClientId = "override-client",
            Tenant = "tenant-from-project",
            Created = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var result = await service.CreateAuthorizeUrlAsync(
            projectId,
            providerId: "microsoft",
            Guid.NewGuid(),
            new ToolOAuthAuthorizeUrlRequest(
                ClientId: "request-client",
                Tenant: "request-tenant",
                Scopes: ["openid", "profile", "openid"],
                RedirectUri: "https://localhost/callback",
                ReturnUrl: "settings/connections"),
            CancellationToken.None);

        result.AuthorizeUrl.Should().Contain("/tenant-from-project/oauth2/v2.0/authorize");
        result.AuthorizeUrl.Should().Contain("client_id=override-client");

        var state = await context.OAuthAuthorizationStates.SingleAsync();
        state.ClientId.Should().Be("override-client");
        state.Tenant.Should().Be("tenant-from-project");
        state.ReturnUrl.Should().Be("/settings/connections");
        state.Scopes.Should().Be("openid profile");
    }

    [TestMethod]
    public async Task CreateAuthorizeUrlAsync_Throws_for_invalid_redirect_uri()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-redirect-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);

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

    [TestMethod]
    public async Task CreateAuthorizeUrlAsync_Throws_when_scopes_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-scopes-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);

        var act = async () => await service.CreateAuthorizeUrlAsync(
            Guid.NewGuid(),
            "microsoft",
            Guid.NewGuid(),
            new ToolOAuthAuthorizeUrlRequest(
                ClientId: "client-id",
                Tenant: "common",
                Scopes: [" ", ""],
                RedirectUri: "https://localhost/callback",
                ReturnUrl: null),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one OAuth scope is required*");
    }

    [TestMethod]
    public async Task CompleteCallbackAsync_Throws_when_state_does_not_match_user_or_provider()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-state-mismatch-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);
        var storedUserId = Guid.NewGuid();

        context.OAuthAuthorizationStates.Add(new OAuthAuthorizationState
        {
            State = "state-mismatch",
            UserId = storedUserId,
            ProviderId = "microsoft",
            CodeVerifier = "verifier",
            ClientId = "client-id",
            Tenant = "common",
            Scopes = "openid",
            RedirectUri = "https://localhost/callback",
            Created = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await context.SaveChangesAsync();

        var act = async () => await service.CompleteCallbackAsync(
            Guid.NewGuid(),
            providerId: "microsoft",
            userId: Guid.NewGuid(),
            code: "auth-code",
            state: "state-mismatch",
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*does not match the authenticated user/provider*");
        (await context.OAuthAuthorizationStates.CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task CompleteCallbackAsync_Throws_when_state_expired_and_removes_row()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-state-expired-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);
        var userId = Guid.NewGuid();

        context.OAuthAuthorizationStates.Add(new OAuthAuthorizationState
        {
            State = "expired-state",
            UserId = userId,
            ProviderId = "microsoft",
            CodeVerifier = "verifier",
            ClientId = "client-id",
            Tenant = "common",
            Scopes = "openid",
            RedirectUri = "https://localhost/callback",
            Created = DateTime.UtcNow.AddMinutes(-20),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await context.SaveChangesAsync();

        var act = async () => await service.CompleteCallbackAsync(
            Guid.NewGuid(),
            providerId: "microsoft",
            userId: userId,
            code: "auth-code",
            state: "expired-state",
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid or expired*");
        (await context.OAuthAuthorizationStates.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task CompleteCallbackAsync_Exchanges_code_persists_token_and_consumes_state_rows()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-callback-success-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var secrets = CreateSecretsOptions();
        var service = CreateService(
            context,
            responder: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "access_token":"issued-access-token",
                      "refresh_token":"issued-refresh-token",
                      "scope":"scope.one scope.two",
                      "expires_in":1800
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            },
            secretsOptions: secrets);

        var projectId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string providerId = "microsoft";

        context.OAuthAuthorizationStates.AddRange(
            new OAuthAuthorizationState
            {
                State = "expired-state",
                UserId = userId,
                ProviderId = providerId,
                CodeVerifier = "verifier-expired",
                ClientId = "client-id",
                Tenant = "common",
                Scopes = "openid profile",
                RedirectUri = "https://localhost/callback",
                Created = DateTime.UtcNow.AddMinutes(-30),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            },
            new OAuthAuthorizationState
            {
                State = "active-state",
                UserId = userId,
                ProviderId = providerId,
                CodeVerifier = "verifier-active",
                ClientId = "client-id",
                Tenant = "common",
                Scopes = "openid profile",
                RedirectUri = "https://localhost/callback",
                Created = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
        await context.SaveChangesAsync();

        var status = await service.CompleteCallbackAsync(
            projectId,
            providerId,
            userId,
            code: "auth-code",
            state: "active-state",
            cancellationToken: CancellationToken.None);

        status.Connected.Should().BeTrue();
        status.Scopes.Should().BeEquivalentTo(["scope.one", "scope.two"]);
        status.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(20));
        (await context.OAuthAuthorizationStates.CountAsync()).Should().Be(0);

        var tokenRow = await context.ExternalOAuthTokens.SingleAsync();
        tokenRow.ProjectId.Should().Be(projectId);
        tokenRow.ProviderId.Should().Be(providerId);
        tokenRow.Scope.Should().Be("scope.one scope.two");
        tokenRow.AccessTokenEncrypted.Should().NotBe("issued-access-token");
        tokenRow.RefreshTokenEncrypted.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task GetStatusAsync_Returns_disconnected_when_no_token()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-status-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);

        var status = await service.GetStatusAsync(Guid.NewGuid(), "microsoft");

        status.Connected.Should().BeFalse();
        status.Scopes.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DisconnectAsync_Is_noop_when_token_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-disconnect-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);

        await service.DisconnectAsync(Guid.NewGuid(), "microsoft");

        (await context.ExternalOAuthTokens.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ResolveExternalAuthTokensForAssistantAsync_Returns_default_bearer_for_host_and_provider()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-resolve-default-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var secrets = CreateSecretsOptions();
        var service = CreateService(context, secretsOptions: secrets);
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();

        var provider = new AssistantAuthProvider
        {
            Id = Guid.NewGuid(),
            ProviderId = "graph-provider",
            AuthType = "oauth",
            ClientId = "client-id",
            Tenant = "common",
            ValueTemplate = null,
            Created = DateTime.UtcNow
        };

        context.Assistants.Add(new Assistant
        {
            Id = assistantId,
            Name = "Resolver Assistant",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow
        });
        context.AssistantAuthProviders.Add(provider);
        context.AssistantOpenApiSchemas.Add(new AssistantOpenApiSchema
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            Name = "graph",
            ApiHost = "graph.microsoft.com",
            SpecificationJson = "{\"openapi\":\"3.0.0\",\"paths\":{}}",
            AuthProviderId = provider.Id,
            Created = DateTime.UtcNow
        });
        context.ExternalOAuthTokens.Add(new ExternalOAuthToken
        {
            UserId = userId,
            ProjectId = projectId,
            ProviderId = provider.ProviderId,
            AccessTokenEncrypted = EncryptToken("access-token", secrets),
            ExpiresAt = DateTime.UtcNow.AddMinutes(20),
            Scope = "openid profile",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var tokens = await service.ResolveExternalAuthTokensForAssistantAsync(
            userId,
            projectId,
            assistantId,
            assistantName: "Resolver Assistant",
            cancellationToken: CancellationToken.None);

        tokens.Should().ContainKey("graph.microsoft.com");
        tokens["graph.microsoft.com"].Should().Be("Bearer access-token");
        tokens.Should().ContainKey("graph-provider");
        tokens["graph-provider"].Should().Be("Bearer access-token");
    }

    [TestMethod]
    public async Task ResolveExternalAuthTokensForAssistantAsync_Refreshes_expiring_token_and_persists_updates()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-refresh-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var secrets = CreateSecretsOptions();
        string? capturedBody = null;
        var service = CreateService(
            context,
            responder: request =>
            {
                capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                var payload = """
                    {
                      "access_token":"new-access",
                      "refresh_token":"new-refresh",
                      "scope":"scope.one scope.two",
                      "expires_in":"1200"
                    }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
            },
            secretsOptions: secrets);

        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        var providerId = "graph-provider";

        var provider = new AssistantAuthProvider
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            AuthType = "oauth",
            ClientId = "client-id",
            Tenant = "common",
            ValueTemplate = "Bearer {access_token}",
            Created = DateTime.UtcNow
        };

        context.Assistants.Add(new Assistant
        {
            Id = assistantId,
            Name = "Refresh Assistant",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow
        });
        context.AssistantAuthProviders.Add(provider);
        context.AssistantOpenApiSchemas.Add(new AssistantOpenApiSchema
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            Name = "graph",
            ApiHost = "graph.microsoft.com",
            SpecificationJson = "{\"openapi\":\"3.0.0\",\"paths\":{}}",
            AuthProviderId = provider.Id,
            Created = DateTime.UtcNow
        });
        context.ExternalOAuthTokens.Add(new ExternalOAuthToken
        {
            UserId = userId,
            ProjectId = projectId,
            ProviderId = providerId,
            AccessTokenEncrypted = EncryptToken("old-access", secrets),
            RefreshTokenEncrypted = EncryptToken("old-refresh", secrets),
            ExpiresAt = DateTime.UtcNow.AddMinutes(2),
            Scope = "scope.old",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var tokens = await service.ResolveExternalAuthTokensForAssistantAsync(
            userId,
            projectId,
            assistantId,
            assistantName: "Refresh Assistant",
            cancellationToken: CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("grant_type=refresh_token");
        tokens["graph.microsoft.com"].Should().Be("Bearer new-access");

        var storedToken = await context.ExternalOAuthTokens.SingleAsync(item => item.ProviderId == providerId);
        storedToken.Scope.Should().Be("scope.one scope.two");
        storedToken.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(15));
    }

    [TestMethod]
    public async Task ResolveExternalAuthTokensForAssistantAsync_Removes_invalid_expiring_token_and_requires_reconnect()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-reconnect-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var secrets = CreateSecretsOptions();
        var service = CreateService(context, secretsOptions: secrets);
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        var providerId = "graph-provider";

        var provider = new AssistantAuthProvider
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            AuthType = "oauth",
            ClientId = "client-id",
            Tenant = "common",
            ValueTemplate = "Bearer {access_token}",
            Created = DateTime.UtcNow
        };

        context.Assistants.Add(new Assistant
        {
            Id = assistantId,
            Name = "Reconnect Assistant",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow
        });
        context.AssistantAuthProviders.Add(provider);
        context.AssistantOpenApiSchemas.Add(new AssistantOpenApiSchema
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            Name = "graph",
            ApiHost = "graph.microsoft.com",
            SpecificationJson = "{\"openapi\":\"3.0.0\",\"paths\":{}}",
            AuthProviderId = provider.Id,
            Created = DateTime.UtcNow
        });
        context.ExternalOAuthTokens.Add(new ExternalOAuthToken
        {
            UserId = userId,
            ProjectId = projectId,
            ProviderId = providerId,
            AccessTokenEncrypted = EncryptToken("old-access", secrets),
            RefreshTokenEncrypted = null,
            ExpiresAt = DateTime.UtcNow.AddMinutes(1),
            Scope = "scope.old",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var act = async () => await service.ResolveExternalAuthTokensForAssistantAsync(
            userId,
            projectId,
            assistantId,
            assistantName: "Reconnect Assistant",
            cancellationToken: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ToolOAuthReconnectRequiredException>();
        ex.Which.ProviderIds.Should().ContainSingle().Which.Should().Be(providerId);
        (await context.ExternalOAuthTokens.CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ResolveExternalAuthTokensForAssistantAsync_Throws_reconnect_when_provider_token_missing()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-missing-token-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var service = CreateService(context);
        var assistantId = Guid.NewGuid();
        var providerId = "graph-provider";

        var provider = new AssistantAuthProvider
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            AuthType = "oauth",
            ClientId = "client-id",
            Tenant = "common",
            Created = DateTime.UtcNow
        };

        context.Assistants.Add(new Assistant
        {
            Id = assistantId,
            Name = "Token Missing Assistant",
            Kind = AssistantKind.Assistant,
            Created = DateTime.UtcNow
        });
        context.AssistantAuthProviders.Add(provider);
        context.AssistantOpenApiSchemas.Add(new AssistantOpenApiSchema
        {
            Id = Guid.NewGuid(),
            AssistantId = assistantId,
            Name = "graph",
            ApiHost = "graph.microsoft.com",
            SpecificationJson = "{\"openapi\":\"3.0.0\",\"paths\":{}}",
            AuthProviderId = provider.Id,
            Created = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var act = async () => await service.ResolveExternalAuthTokensForAssistantAsync(
            userId: Guid.NewGuid(),
            projectId: Guid.NewGuid(),
            assistantId,
            assistantName: "Token Missing Assistant",
            cancellationToken: CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ToolOAuthReconnectRequiredException>();
        ex.Which.ProviderIds.Should().ContainSingle().Which.Should().Be(providerId);
    }

    [TestMethod]
    public async Task ResolveExternalAuthTokensForAssistantAsync_Uses_assistant_name_lookup_and_project_override_for_refresh()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"tool-oauth-name-lookup-{Guid.NewGuid():N}");
        await using var context = new ApplicationDbContext(options);
        var secrets = CreateSecretsOptions();
        Uri? capturedUri = null;
        string? capturedBody = null;
        var service = CreateService(
            context,
            responder: request =>
            {
                capturedUri = request.RequestUri;
                capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "access_token":"refreshed-token",
                          "refresh_token":"refreshed-refresh",
                          "scope":"scope.one scope.two",
                          "expires_in":1200
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            },
            secretsOptions: secrets);

        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var localAssistantId = Guid.NewGuid();
        const string assistantName = "Shared Assistant";
        const string providerId = "api-provider";

        var provider = new AssistantAuthProvider
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            AuthType = "oauth",
            ClientId = "provider-client",
            Tenant = null,
            ValueTemplate = "Token {access_token}",
            Created = DateTime.UtcNow
        };

        context.Assistants.AddRange(
            new Assistant
            {
                Id = localAssistantId,
                Name = assistantName,
                Kind = AssistantKind.Assistant,
                IsGlobal = false,
                IsActive = true,
                Created = DateTime.UtcNow
            },
            new Assistant
            {
                Id = Guid.NewGuid(),
                Name = assistantName,
                Kind = AssistantKind.Assistant,
                IsGlobal = true,
                IsActive = true,
                Created = DateTime.UtcNow
            });
        context.AssistantAuthProviders.Add(provider);
        context.AssistantOpenApiSchemas.Add(new AssistantOpenApiSchema
        {
            Id = Guid.NewGuid(),
            AssistantId = localAssistantId,
            Name = "api",
            ApiHost = "api.local.example",
            SpecificationJson = "{\"openapi\":\"3.0.0\",\"paths\":{}}",
            AuthProviderId = provider.Id,
            Created = DateTime.UtcNow
        });
        context.ProjectExternalAuths.Add(new ProjectExternalAuth
        {
            ProjectId = projectId,
            ProviderId = providerId,
            AuthType = "oauth",
            ClientId = "override-client",
            Tenant = "override-tenant",
            Created = DateTime.UtcNow
        });
        context.ExternalOAuthTokens.Add(new ExternalOAuthToken
        {
            UserId = userId,
            ProjectId = projectId,
            ProviderId = providerId,
            AccessTokenEncrypted = EncryptToken("old-token", secrets),
            RefreshTokenEncrypted = EncryptToken("refresh-token", secrets),
            ExpiresAt = DateTime.UtcNow.AddMinutes(1),
            Scope = "scope.old",
            Created = DateTime.UtcNow,
            Updated = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var tokens = await service.ResolveExternalAuthTokensForAssistantAsync(
            userId,
            projectId,
            assistantId: null,
            assistantName,
            cancellationToken: CancellationToken.None);

        capturedUri.Should().NotBeNull();
        capturedUri!.AbsoluteUri.Should().Contain("/override-tenant/oauth2/v2.0/token");
        capturedBody.Should().Contain("client_id=override-client");
        tokens["api.local.example"].Should().Be("Token refreshed-token");
        tokens[providerId].Should().Be("Token refreshed-token");
    }

    private static ToolOAuthService CreateService(
        ApplicationDbContext context,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        SettingsSecretsOptions? secretsOptions = null)
    {
        var handler = new StaticHttpMessageHandler((request, _) =>
            Task.FromResult(responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var optionsMonitor = new Mock<IOptionsMonitor<SettingsSecretsOptions>>();
        optionsMonitor.SetupGet(x => x.CurrentValue).Returns(secretsOptions ?? CreateSecretsOptions());

        return new ToolOAuthService(
            context,
            new FakeHttpClientFactory(handler),
            optionsMonitor.Object,
            NullLogger<ToolOAuthService>.Instance);
    }

    private static SettingsSecretsOptions CreateSecretsOptions()
    {
        return new SettingsSecretsOptions
        {
            ActiveKeyId = "tests",
            Keys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tests"] = "MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY="
            }
        };
    }

    private static string EncryptToken(string plaintext, SettingsSecretsOptions options)
    {
        var definition = new SettingsSectionDefinition
        {
            SectionName = "ToolOAuthTokens",
            Properties =
            [
                new SettingsPropertyDefinition("value", "ToolOAuthTokens:value", IsSecret: true)
            ]
        };
        var payload = new JsonObject { ["value"] = plaintext };
        var encrypted = ApplicationSettingsJson.EncryptSecrets(definition, payload, options);
        return ApplicationSettingsJson.NodeToString(encrypted["value"]);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }
    }

    private sealed class StaticHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
}

