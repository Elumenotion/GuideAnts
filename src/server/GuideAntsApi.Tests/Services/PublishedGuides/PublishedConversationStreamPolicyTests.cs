using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Tests.BackgroundJobs;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services.PublishedGuides;

[TestClass]
public sealed class PublishedConversationStreamPolicyTests
{
    [TestMethod]
    public async Task ResolveUserIdentityAsync_Returns_internal_id_for_app_identity()
    {
        var options = BackgroundJobTestHelpers.CreateInMemoryOptions($"pub-stream-policy-{Guid.NewGuid():N}");
        var userId = Guid.NewGuid();
        await using (var context = new ApplicationDbContext(options))
        {
            context.Users.Add(new User
            {
                Id = userId,
                Name = "Stream Policy User",
                Email = "stream-policy@example.com",
                PasswordHash = "hash",
                SecurityStamp = Guid.NewGuid()
            });
            await context.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton(new TestDbContextFactory(options));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp => sp.GetRequiredService<TestDbContextFactory>());
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();
        var policy = new PublishedConversationStreamPolicy(provider.GetRequiredService<IServiceScopeFactory>());

        var identity = await policy.ResolveUserIdentityAsync(userId, userId.ToString(), CancellationToken.None);

        identity.UserId.Should().Be(userId);
        identity.UserName.Should().Be("Stream Policy User");
        identity.ExternalUserIdentity.Should().Be(userId.ToString());
    }
}
