using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Bootstrap;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiStackHostResolverRowOwnedTests
{
    [TestMethod]
    public async Task GetAllConfiguredStackBasesAsync_IncludesRowOwnedLlamaInstances()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://local:8080/llama-cpp",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://local:8080",
            })
            .Build();

        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"rowowned-{Guid.NewGuid():N}")
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        db.Models.Add(new Model
        {
            ModelId = "qwen3.6-35b-a3b-mtp-max",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = """{"routerModelId":"Qwen3.6-35B-A3B-MTP-GGUF-Max","stackBaseUrl":"http://192.0.2.1:8112"}""",
        });
        db.Models.Add(new Model
        {
            ModelId = "qwen3.6-35b-a3b-mtp-local",
            Provider = "llama-cpp",
            IsActive = true,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = """{"routerModelId":"Qwen3.6-35B-A3B-MTP-GGUF"}""",
        });
        // Inactive row: must not contribute its base.
        db.Models.Add(new Model
        {
            ModelId = "inactive-model",
            Provider = "llama-cpp",
            IsActive = false,
            Created = DateTime.UtcNow,
            RuntimeConfigJson = """{"routerModelId":"off","stackBaseUrl":"http://10.0.0.9:8112"}""",
        });
        await db.SaveChangesAsync();

        var resolver = new LocalAiStackHostResolver(
            configuration,
            new ScopeFactoryStub(dbOptions));

        var bases = (await resolver.GetAllConfiguredStackBasesAsync()).ToList();

        bases.Should().HaveCount(2);
        bases.Should().Contain("http://local:8080");
        bases.Should().Contain("http://192.0.2.1:8112");
    }

    [TestMethod]
    public async Task GetAllConfiguredStackBasesAsync_DownDb_DegradesToConfigBases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://local:8080/llama-cpp",
            })
            .Build();

        var resolver = new LocalAiStackHostResolver(
            configuration,
            new ScopeFactoryStub(dbAvailable: false));

        var bases = (await resolver.GetAllConfiguredStackBasesAsync()).ToList();

        bases.Should().BeEquivalentTo(new[] { "http://local:8080" });
    }

    [TestMethod]
    public void GetAllConfiguredStackBases_Sync_DoesNotIncludeRowOwnedBases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://local:8080/llama-cpp",
            })
            .Build();

        var resolver = new LocalAiStackHostResolver(
            configuration,
            new ScopeFactoryStub(dbAvailable: false));

        // The sync API is the config-only view (used by HasAnyConfiguredStack and
        // service-to-stack routing); row-owned instances require the async API.
        resolver.GetAllConfiguredStackBases().ToList().Should().BeEquivalentTo(new[] { "http://local:8080" });
    }

    private sealed class ScopeFactoryStub : IServiceScopeFactory
    {
        private readonly DbContextOptions<ApplicationDbContext>? _dbOptions;

        public ScopeFactoryStub(DbContextOptions<ApplicationDbContext>? dbOptions = null, bool dbAvailable = true)
        {
            _dbOptions = dbAvailable ? dbOptions : null;
        }

        public IServiceScope CreateScope() => new ScopeStub(_dbOptions);
    }

    private sealed class ScopeStub : IServiceScope, IDisposable
    {
        private readonly ServiceProvider _provider;

        public ScopeStub(DbContextOptions<ApplicationDbContext>? dbOptions)
        {
            var services = new ServiceCollection();
            if (dbOptions is not null)
            {
                services.AddSingleton(dbOptions);
                services.AddScoped<ApplicationDbContext>();
            }

            _provider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider => _provider;

        public void Dispose() => _provider.Dispose();
    }
}
