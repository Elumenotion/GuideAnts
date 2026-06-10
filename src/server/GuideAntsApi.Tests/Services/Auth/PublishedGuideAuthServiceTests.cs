using FluentAssertions;
using GuideAntsApi.DataModel;
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
}
