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
    private const string GlobalLlamaStackBase = GlobalBase;
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

    [TestMethod]
    public async Task OverrideAllOff_KeepsOtherLoadedAliases_AndStillLoadsTheDefault()
    {
        // Regression for the S1 defect: with override-all off, the default model is the
        // fallback for entities that name no model of their own (ChatModelResolver
        // "defaulted to"), so it must be warm. What must NOT happen is the sweep that
        // unloads every other loaded alias — that rule is global-only.
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: "{\"routerModelId\":\"QwenA\"}"),
            (id: "muse-max", provider: "llama-cpp", json: "{\"routerModelId\":\"Muse\",\"stackBaseUrl\":\"http://192.0.2.1:8112\"}"),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                // The default's alias is NOT loaded on its own instance: the load must
                // still happen with the flag off.
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "muse-max", out _, overrideAll: false);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().BeEmpty();
        clients[MaxBase].Unloaded.Should().BeEmpty();
        clients[MaxBase].Loaded.Should().ContainSingle().Which.Should().Be("Muse");
        clients[GlobalBase].LoadedAliases.Should().Contain("QwenA");
        clients[MaxBase].LoadedAliases.Should().Contain("Muse");
    }

    [TestMethod]
    public async Task OverrideAllOff_WithCloudDefault_UnloadsNothing()
    {
        // Nothing to load, and the sweep is the global rule: with the flag off a cloud
        // default implies nothing about the loaded set, so every alias is left alone.
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: "{\"routerModelId\":\"QwenA\"}"),
            (id: "gpt-cloud", provider: "openai", json: null),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "gpt-cloud", out _, overrideAll: false);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().BeEmpty();
        clients[GlobalBase].Loaded.Should().BeEmpty();
        clients[GlobalBase].LoadedAliases.Should().Contain("QwenA");
    }

    [TestMethod]
    public async Task OverrideAllOn_WithCloudDefault_StillUnloadsEverything()
    {
        // Guards the branch split: making override-all-off a no-op must not weaken the
        // global rule.
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: "{\"routerModelId\":\"QwenA\"}"),
            (id: "gpt-cloud", provider: "openai", json: null),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "gpt-cloud", out var aliasState, overrideAll: true);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().ContainSingle().Which.Should().Be("QwenA");
        aliasState.Cleared.Should().BeTrue();
    }

   
    [TestMethod]
    public async Task OverrideAllOff_GlobalStackDefault_IsLoadedOnTheDeploymentInstance()
    {
        // A llama row with no row-owned stackBaseUrl lives on the deployment's own
        // stack (LlamaCpp:BaseUrl): the single-stack case, which is the common one.
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: "{\"routerModelId\":\"QwenA\"}"),
            (id: "local-def", provider: "llama-cpp", json: "{\"routerModelId\":\"LocalDef\"}"),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "local-def", out _, overrideAll: false);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Loaded.Should().ContainSingle().Which.Should().Be("LocalDef");
        clients[GlobalBase].Unloaded.Should().BeEmpty();
        clients[GlobalBase].LoadedAliases.Should().Contain("QwenA");
        clients[MaxBase].Loaded.Should().BeEmpty();
    }

    [TestMethod]
    public async Task OverrideAllOn_GlobalStackDefault_SweepsEveryInstanceThenLoadsIt()
    {
        var db = await CreateDbAsync(rows: [
            (id: "qwen-a", provider: "llama-cpp", json: "{\"routerModelId\":\"QwenA\"}"),
            (id: "local-def", provider: "llama-cpp", json: "{\"routerModelId\":\"LocalDef\"}"),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = ["Muse"],
            });

        var reconciler = CreateReconciler(db, provider, defaultModelId: "local-def", out _, overrideAll: true);

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Unloaded.Should().ContainSingle().Which.Should().Be("QwenA");
        clients[GlobalBase].Loaded.Should().ContainSingle().Which.Should().Be("LocalDef");
        clients[MaxBase].Unloaded.Should().ContainSingle().Which.Should().Be("Muse");
    }

    [TestMethod]
    public async Task GlobalStackDefaultWithoutUsableLlamaBaseUrl_DeclinesToAct()
    {
        // No usable LlamaCpp:BaseUrl: the default's stack is not addressable, so the
        // reconciler must decline rather than guess an instance. With override-all off
        // the sweep is not implied either, so the loaded set is left alone.
        var db = await CreateDbAsync(rows: [
            (id: "local-def", provider: "llama-cpp", json: "{\"routerModelId\":\"LocalDef\"}"),
        ]);
        var (provider, clients) = CreateClientProvider(
            initialLoaded: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [GlobalBase] = ["QwenA"],
                [MaxBase] = [],
            });

        var reconciler = CreateReconciler(
            db, provider, defaultModelId: "local-def", out _,
            overrideAll: false, globalLlamaStackBase: "");

        await reconciler.ReconcileWithGlobalDefaultAsync();

        clients[GlobalBase].Loaded.Should().BeEmpty();
        clients[GlobalBase].Unloaded.Should().BeEmpty();
        clients[GlobalBase].LoadedAliases.Should().Contain("QwenA");
        clients[MaxBase].Loaded.Should().BeEmpty();
    }

    private static GlobalDefaultLlamaReconciler CreateReconciler(
        ApplicationDbContext db,
        FakeClientProvider provider,
        string? defaultModelId,
        out FakeNotebookAliasState aliasState,
        bool overrideAll = true,
        string? globalLlamaStackBase = null)
    {
        aliasState = new FakeNotebookAliasState();
        // Faithful to production: the universe is keyed by CanonicalInstanceKey of the
        // NORMALIZED stack base, and the deployment's own llama stack is published
        // through GetStackBaseForService(llama) - the target of a row without a
        // row-owned stack. Pass globalLlamaStackBase: "" to model a deployment with no
        // usable LlamaCpp:BaseUrl, where that stack is absent from the universe.
        var globalStackBase = globalLlamaStackBase switch
        {
            null => LocalAiStackHostUrls.NormalizeStackBaseUrl(GlobalLlamaStackBase),
            _ when string.IsNullOrWhiteSpace(globalLlamaStackBase) => null,
            _ => LocalAiStackHostUrls.NormalizeStackBaseUrl(globalLlamaStackBase),
        };
        var instances = new List<LocalAiInstance>
        {
            new LocalAiInstance(LocalAiStackHostResolver.CanonicalInstanceKey(MaxBase), MaxBase),
        };
        if (globalStackBase is not null)
        {
            instances.Insert(0, new LocalAiInstance(
                LocalAiStackHostResolver.CanonicalInstanceKey(globalStackBase), GlobalLlamaStackBase));
        }

        var resolver = new Mock<ILocalAiStackHostResolver>();
        resolver
            .Setup(r => r.GetStackBaseForService(LocalAiStackHostUrls.LlamaServiceId))
            .Returns(globalStackBase);
        resolver
            .Setup(r => r.GetAllConfiguredInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(instances);

 var defaultStore = new FakeChatDefaultsStore(defaultModelId, overrideAll);
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
        public FakeChatDefaultsStore(string? defaultModelId, bool overrideAll)
        {
            Current = new ChatDefaultsSnapshot(defaultModelId, overrideAll, null, null, null, null);
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
