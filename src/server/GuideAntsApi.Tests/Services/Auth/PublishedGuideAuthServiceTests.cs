using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Services.Auth;

[TestClass]
public sealed class PublishedGuideAuthServiceTests
{
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

        var service = new PublishedGuideAuthService(
            scopeFactory,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PublishedGuideAuthService>.Instance);

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
    public async Task ValidateAsync_Allows_anonymous_when_no_webhook_or_api_key()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-anon-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, apiKeyHash: null, webhookUrl: null);
        var service = CreateService(options);

        var result = await service.ValidateAsync(pubId, authorizationHeader: null, Guid.NewGuid(), notebookId, CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.UserIdentity.Should().BeNull();
    }

    [TestMethod]
    public async Task ValidateAsync_Requires_api_key_when_hash_configured()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-key-req-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, apiKeyHash: PublishedGuideAuthService.HashApiKey("gak_test"), webhookUrl: null);
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
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthService.HashApiKey(validKey), webhookUrl: null);
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
        var (_, notebookId) = await SeedPublishedGuideAsync(options, pubId, PublishedGuideAuthService.HashApiKey(apiKey), webhookUrl: null);
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
    }

    [TestMethod]
    public async Task ValidateAsync_Requires_auth_header_when_webhook_configured()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-guide-webhook-{Guid.NewGuid():N}");
        var pubId = Guid.NewGuid();
        var (_, notebookId) = await SeedPublishedGuideAsync(
            options,
            pubId,
            apiKeyHash: null,
            webhookUrl: "https://auth.example.com/validate");
        var service = CreateService(options);

        var result = await service.ValidateAsync(pubId, authorizationHeader: null, Guid.NewGuid(), notebookId, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("authentication_required");
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
            NullLogger<PublishedGuideAuthService>.Instance);
    }

    private static async Task<(Guid ProjectId, Guid NotebookId)> SeedPublishedGuideAsync(
        DbContextOptions<ApplicationDbContext> options,
        Guid pubId,
        string? apiKeyHash,
        string? webhookUrl)
    {
        await using var context = new ApplicationDbContext(options);
        var (projectId, notebookId) = await BackgroundJobTestHelpers.SeedProjectNotebookAsync(context);
        context.PublishedGuides.Add(new PublishedGuide
        {
            Id = pubId,
            GuideId = Guid.NewGuid(),
            NotebookId = notebookId,
            Active = true,
            ApiKeyHash = apiKeyHash,
            AuthValidationWebhookUrl = webhookUrl
        });
        await context.SaveChangesAsync();
        return (projectId, notebookId);
    }
}
