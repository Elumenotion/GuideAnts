using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.PublishedGuides;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAntsApi.Tests.BackgroundJobs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.PublishedWireApi;

[TestClass]
public sealed class PublishedApiExecutionContextResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_Allows_anonymous_when_auth_not_configured()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"wire-api-anon-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, apiKeyHash: null, webhookUrl: null);

        await using var db = new ApplicationDbContext(options);
        var auth = new FakePublishedGuideAuthService();
        var limits = new FakePublishedGuideCostLimitService(allowed: true);
        var resolver = new PublishedApiExecutionContextResolver(db, auth, limits, NullLogger<PublishedApiExecutionContextResolver>.Instance);
        var ctx = new DefaultHttpContext();

        var result = await resolver.ResolveAsync(ctx, pubId, endpointName: "models", ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Context.Should().NotBeNull();
        result.Context!.NotebookId.Should().Be(notebookId);
        result.Context.AuthMode.Should().Be(PublishedApiAuthMode.Anonymous);
        auth.CallCount.Should().Be(0);
    }

    [TestMethod]
    public async Task ResolveAsync_Uses_x_guideants_apikey_header_for_api_key_mode()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"wire-api-key-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        await SeedPublishedGuideAsync(
            options,
            pubId,
            apiKeyHash: PublishedGuideAuthService.HashApiKey("gak_test"),
            webhookUrl: null);

        await using var db = new ApplicationDbContext(options);
        var auth = new FakePublishedGuideAuthService { NextResult = new AuthValidationResult { IsValid = true, UserIdentity = "api-key-user" } };
        var limits = new FakePublishedGuideCostLimitService(allowed: true);
        var resolver = new PublishedApiExecutionContextResolver(db, auth, limits, NullLogger<PublishedApiExecutionContextResolver>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName] = "gak_test";

        var result = await resolver.ResolveAsync(ctx, pubId, endpointName: "models", ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        auth.LastApiKeyHeader.Should().Be("gak_test");
        auth.LastAuthorizationHeader.Should().BeNull();
        result.Context!.AuthMode.Should().Be(PublishedApiAuthMode.ApiKey);
        result.Context.ExternalUserIdentity.Should().Be("api-key-user");
    }

    [TestMethod]
    public async Task ResolveAsync_Uses_bearer_token_for_webhook_mode()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"wire-api-webhook-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        await SeedPublishedGuideAsync(
            options,
            pubId,
            apiKeyHash: null,
            webhookUrl: "https://example.com/auth");

        await using var db = new ApplicationDbContext(options);
        var auth = new FakePublishedGuideAuthService { NextResult = new AuthValidationResult { IsValid = true, UserIdentity = "web-user" } };
        var limits = new FakePublishedGuideCostLimitService(allowed: true);
        var resolver = new PublishedApiExecutionContextResolver(db, auth, limits, NullLogger<PublishedApiExecutionContextResolver>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer webhook-token";

        var result = await resolver.ResolveAsync(ctx, pubId, endpointName: "models", ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        auth.LastAuthorizationHeader.Should().Be("webhook-token");
        auth.LastApiKeyHeader.Should().BeNull();
        result.Context!.AuthMode.Should().Be(PublishedApiAuthMode.Webhook);
        result.Context.ExternalUserIdentity.Should().Be("web-user");
    }

    [TestMethod]
    public async Task ResolveAsync_Validates_app_identity_when_auth_mode_is_app_identity()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"wire-api-appid-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        await SeedPublishedGuideAsync(
            options,
            pubId,
            authMode: PublishedGuideAuthMode.AppIdentity);

        await using var db = new ApplicationDbContext(options);
        var auth = new FakePublishedGuideAuthService
        {
            NextResult = new AuthValidationResult { IsValid = true, UserIdentity = "user-123" }
        };
        var limits = new FakePublishedGuideCostLimitService(allowed: true);
        var resolver = new PublishedApiExecutionContextResolver(db, auth, limits, NullLogger<PublishedApiExecutionContextResolver>.Instance);
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "user-123")
            ], authenticationType: "Cookies"))
        };

        var result = await resolver.ResolveAsync(ctx, pubId, endpointName: "models", ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Context!.AuthMode.Should().Be(PublishedApiAuthMode.AppIdentity);
        result.Context.ExternalUserIdentity.Should().Be("user-123");
        auth.CallCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ResolveAsync_Returns_openai_shaped_limit_error_when_cost_limit_exceeded()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"wire-api-limit-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        await SeedPublishedGuideAsync(options, pubId, apiKeyHash: null, webhookUrl: null);

        await using var db = new ApplicationDbContext(options);
        var auth = new FakePublishedGuideAuthService();
        var limits = new FakePublishedGuideCostLimitService(
            allowed: false,
            reason: "daily_limit_exceeded",
            dailyLimit: 2.5m,
            dailyCharge: 2.7m);
        var resolver = new PublishedApiExecutionContextResolver(db, auth, limits, NullLogger<PublishedApiExecutionContextResolver>.Instance);
        var ctx = new DefaultHttpContext();

        var result = await resolver.ResolveAsync(ctx, pubId, endpointName: "models", ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorResult.Should().NotBeNull();
        var executed = await ExecuteResultAsync(result.ErrorResult!);

        executed.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("insufficient_quota");
        json.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("insufficient_quota");
    }

    [TestMethod]
    public async Task ResolveAsync_Returns_request_too_large_when_content_length_exceeds_endpoint_limit()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"wire-api-size-limit-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(
            options,
            pubId,
            apiKeyHash: null,
            webhookUrl: null,
            wireApiConfig: new PublishedWireApiConfigDto
            {
                Enabled = true,
                MaxRequestSizes = new PublishedWireApiMaxRequestSizesDto
                {
                    ChatCompletionsBytes = 64
                }
            });

        await using var db = new ApplicationDbContext(options);
        var auth = new FakePublishedGuideAuthService();
        var limits = new FakePublishedGuideCostLimitService(allowed: true);
        var resolver = new PublishedApiExecutionContextResolver(db, auth, limits, NullLogger<PublishedApiExecutionContextResolver>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentLength = 65;

        var result = await resolver.ResolveAsync(ctx, pubId, endpointName: "chat.completions", ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorResult.Should().NotBeNull();
        var executed = await ExecuteResultAsync(result.ErrorResult!);
        executed.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        using var json = JsonDocument.Parse(executed.Body);
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("request_too_large");
    }

    private static async Task<(Guid ProjectId, Guid NotebookId)> SeedPublishedGuideAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid pubId,
        string? apiKeyHash = null,
        string? webhookUrl = null,
        PublishedGuideAuthMode? authMode = null,
        PublishedWireApiConfigDto? wireApiConfig = null)
    {
        await using var context = new ApplicationDbContext(options);
        var (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(context);
        wireApiConfig ??= new PublishedWireApiConfigDto { Enabled = true };
        var resolvedAuthMode = authMode ?? ResolveAuthModeForSeed(apiKeyHash, webhookUrl);

        context.PublishedGuides.Add(new PublishedGuide
        {
            Id = pubId,
            GuideId = Guid.NewGuid(),
            NotebookId = notebookId,
            Active = true,
            AuthMode = resolvedAuthMode,
            ApiKeyHash = apiKeyHash,
            AuthValidationWebhookUrl = webhookUrl,
            WireApiConfigJson = JsonSerializer.Serialize(wireApiConfig)
        });
        await context.SaveChangesAsync();
        return (projectId, notebookId);
    }

    private static PublishedGuideAuthMode ResolveAuthModeForSeed(string? apiKeyHash, string? webhookUrl)
    {
        if (!string.IsNullOrWhiteSpace(apiKeyHash))
        {
            return PublishedGuideAuthMode.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(webhookUrl))
        {
            return PublishedGuideAuthMode.Webhook;
        }

        return PublishedGuideAuthMode.Anonymous;
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();

        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (httpContext.Response.StatusCode, body);
    }

    private sealed class FakePublishedGuideAuthService : IPublishedGuideAuthService
    {
        public int CallCount { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }
        public string? LastApiKeyHeader { get; private set; }
        public AuthValidationResult NextResult { get; set; } = new() { IsValid = true };

        public Task<AuthValidationResult> ValidateAsync(
            Guid pubId,
            string? authorizationHeader,
            Guid projectId,
            Guid notebookId,
            CancellationToken ct = default,
            string? apiKeyHeader = null,
            string? appAuthCookieToken = null)
        {
            CallCount++;
            LastAuthorizationHeader = authorizationHeader;
            LastApiKeyHeader = apiKeyHeader;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakePublishedGuideCostLimitService : IPublishedGuideCostLimitService
    {
        private readonly PublishedGuideCostLimitResult _result;

        public FakePublishedGuideCostLimitService(
            bool allowed,
            string? reason = null,
            decimal? dailyLimit = null,
            decimal dailyCharge = 0m)
        {
            var now = DateTime.UtcNow;
            var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
            _result = new PublishedGuideCostLimitResult(
                Allowed: allowed,
                Reason: reason,
                DailyLimitUsd: dailyLimit,
                DailyChargeUsd: dailyCharge,
                DailyWindowStartUtc: dayStart,
                DailyWindowEndUtc: dayStart.AddDays(1),
                BillingPeriodLimitUsd: null,
                BillingPeriodChargeUsd: 0m,
                BillingPeriodStartUtc: null,
                BillingPeriodEndUtc: null);
        }

        public Task<PublishedGuideCostLimitResult> EnsureWithinLimitsAsync(Guid notebookId, CancellationToken ct = default) =>
            Task.FromResult(_result);
    }
}
