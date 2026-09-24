using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class GlobalDefaultLlamaReconcilerTests
{
    private const string GlobalBase = "http://guideants-ai:80";
    private const string MaxBase = "http://192.0.2.1:8112";

    [TestMethod]
    public async Task DefaultChange_UnloadsNonDefaultOnEveryInstance_KeepsLoadedDefault()
    {
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: """{"routerModelId":"QwenA"}"""),
            (id: "qwen-b", provider: "llama-cpp", json: """{"routerModelId":"QwenB"}"""),
            (id: "muse-max", provider: "llama-cpp", json: """{"routerModelId":"Muse","stackBaseUrl":"http://192.0.2.1:8112"}"""),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA", "QwenB"],
                [MaxBase] = ["Muse"],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "muse-max", out _);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().ContainInOrder("QwenA", "QwenB");
        clients[GlobalBase].Loaded.Should().BeEmpty();
        clients[MaxBase].Loaded.Should().BeEmpty();
        clients[MaxBase].Unloaded.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DefaultChange_WhenDefaultNotLoaded_LoadsItOnItsInstanceOnly()
    {
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: """{"routerModelId":"QwenA"}"""),
            (id: "muse-max", provider: "llama-cpp", json: """{"routerModelId":"Muse","stackBaseUrl":"http://192.0.2.1:8112"}"""),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "muse-max", out _);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().ContainSingle().Which.Should().Be("QwenA");
        clients[GlobalBase].Loaded.Should().BeEmpty();
        clients[MaxBase].Loaded.Should().ContainSingle().Which.Should().Be("Muse");
        clients[MaxBase].Unloaded.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DefaultChange_ToCloud_UnloadsEveryLoadedAlias()
    {
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: """{"routerModelId":"QwenA"}"""),
            (id: "muse-max", provider: "llama-cpp", json: """{"routerModelId":"Muse","stackBaseUrl":"http://192.0.2.1:8112"}"""),
            (id: "gpt-cloud", provider: "openai", json: null),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = ["Muse"],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "gpt-cloud", out _);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().ContainSingle().Which.Should().Be("QwenA");
        clients[MaxBase].Unloaded.Should().ContainSingle().Which.Should().Be("Muse");
        clients[GlobalBase].Loaded.Should().BeEmpty();
        clients[MaxBase].Loaded.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NoDefault_UnloadsEverythingAndLoadsNothing()
    {
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: """{"routerModelId":"QwenA"}"""),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: null, out _);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().ContainSingle().Which.Should().Be("QwenA");
        clients[GlobalBase].Loaded.Should().BeEmpty();
        clients[MaxBase].Loaded.Should().BeEmpty();
        clients[MaxBase].Unloaded.Should().BeEmpty();
    }

    private static GlobalDefaultLlamaReconciler CreateReconciler(
        ApplicationDbContext db,
        FakeClientProvider provider,
        string? defaultModelId,
        out FakeNotebookAliasState aliasState)
    {
        aliasState = new FakeNotebookAliasState();
        var resolver = new Mock<ILocalAiStackHostResolver>();
        resolver
            .Setup(r => r.GetAllConfiguredInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new LocalAiInstance("guideants-ai:80", GlobalBase),
                new LocalAiInstance("192.0.2.1:8112", MaxBase),
            });

        var defaultStore = new FakeChatDefaultsStore(defaultModelId);
        var scopeFactory = new TestScopeFactory(db, defaultStore);
        var coordinator = new LlamaRuntimeCoordinator();

        return new GlobalDefaultLlamaReconciler(
            scopeFactory,
            resolver.Object,
            provider,
            aliasState,
            coordinator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalDefaultLlamaReconciler>.Instance);
    }

    private static async Task<ApplicationDbContext> CreateDbAsync((string id, string provider, string? json)[] rows)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"default-{Guid.NewGuid():N}")
            .Options;
        var db = new ApplicationDbContext(options);
        foreach (var (id, provider, json) in rows)
        {
            db.Models.Add(new Model
            {
                ModelId = id,
                DisplayName = id,
                Provider = provider,
                IsActive = true,
                Created = DateTime.UtcNow,
                RuntimeConfigJson = json,
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static (FakeClientProvider Provider, Dictionary<string, FakeLlamaClient> Clients) CreateClientProvider(
        Dictionary<string, string[]> initialLoaded)
    {
        var clients = initialLoaded.ToDictionary(
            kv => kv.Key,
            kv => new FakeLlamaClient(kv.Value));
        var provider = new FakeClientProvider(clients);
        return (provider, clients);
    }

    private sealed class FakeLlamaClient : ILlamaServerRuntimeClient
    {
        public FakeLlamaClient(string[] initial)
        {
            LoadedAliases = new List<string>(initial);
        }

        public List<string> LoadedAliases { get; }
        public List<string> Loaded { get; } = new();
        public List<string> Unloaded { get; } = new();

        public Task<LlamaModelsResponse> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlamaModelsResponse
            {
                Data = LoadedAliases
                    .Select(alias => new LlamaModelData
                    {
                        Id = alias,
                        Status = new LlamaModelStatus { Value = "loaded" },
                    })
                    .ToList(),
            });

        public Task LoadModelAsync(string modelPathOrPreset, CancellationToken cancellationToken = default)
        {
            Loaded.Add(modelPathOrPreset);
            if (!LoadedAliases.Contains(modelPathOrPreset, StringComparer.Ordinal))
            {
                LoadedAliases.Add(modelPathOrPreset);
            }

            return Task.CompletedTask;
        }

        public Task UnloadModelAsync(string routerModelId, CancellationToken cancellationToken = default)
        {
            Unloaded.Add(routerModelId);
            LoadedAliases.RemoveAll(a => a == routerModelId);
            return Task.CompletedTask;
        }

        public Task<LlamaOpenAiModelsResponse> ListOpenAiModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlamaOpenAiModelsResponse());
    }

    private sealed class FakeClientProvider : ILlamaStackRuntimeClientProvider
    {
        public FakeClientProvider(Dictionary<string, FakeLlamaClient> clients)
        {
            _clients = clients;
            _global = clients.First(kv => kv.Key == GlobalBase).Value;
        }

        private readonly Dictionary<string, FakeLlamaClient> _clients;
        private readonly FakeLlamaClient _global;

        public ILlamaServerRuntimeClient? GetClientForStack(string? stackBaseUrl, string? stackApiKey) =>
            string.IsNullOrWhiteSpace(stackBaseUrl)
                ? null
                : _clients[stackBaseUrl.TrimEnd('/')];

        public ILlamaServerRuntimeClient Global => _global;
    }

    private sealed class FakeNotebookAliasState : INotebookChatAliasState
    {
        public bool Cleared { get; private set; }

        public string? ActiveChatAlias => null;
        public string? GetActiveChatAlias(string? instanceBase) => null;
        public IReadOnlyDictionary<string, string> GetActiveChatAliases() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public void SetActiveChatAlias(string routerAlias) { }
        public void SetActiveChatAliasForInstance(string? instanceBase, string routerAlias) { }
        public void ClearActiveChatAlias(string? routerAlias = null) => Cleared = true;
        public void ClearInstance(string? instanceBase) { }
    }

    private sealed class FakeChatDefaultsStore : IChatDefaultsStore
    {
        public FakeChatDefaultsStore(string? defaultModelId)
        {
            Current = new ChatDefaultsSnapshot(defaultModelId, false, null, null, null, null);
        }

        public ChatDefaultsSnapshot Current { get; }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestScopeFactory : IServiceScopeFactory
    {
        private readonly ApplicationDbContext _db;
        private readonly IChatDefaultsStore _store;

        public TestScopeFactory(ApplicationDbContext db, IChatDefaultsStore store)
        {
            _db = db;
            _store = store;
        }

        public IServiceScope CreateScope() => new TestScope(_db, _store);

        private sealed class TestScope : IServiceScope
        {
            private readonly ApplicationDbContext _db;
            private readonly IChatDefaultsStore _store;

            public TestScope(ApplicationDbContext db, IChatDefaultsStore store)
            {
                _db = db;
                _store = store;
                var services = new ServiceCollection();
                services.AddSingleton(_db);
                services.AddSingleton<IChatDefaultsStore>(_store);
                ServiceProvider = services.BuildServiceProvider();
            }

            public IServiceProvider ServiceProvider { get; }
            public void Dispose() => (ServiceProvider as IDisposable)?.Dispose();
        }
    }
}
