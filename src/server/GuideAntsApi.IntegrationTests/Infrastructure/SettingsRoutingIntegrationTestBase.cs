using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.TestUtils;
using System.Net.Http.Headers;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for Phase G settings + routing integration tests. Spins up the
/// <see cref="SettingsRoutingTestWebApplicationFactory"/> (stubbed llama
/// runtime client + stubbed router-models.ini) once per class and exposes
/// accessors to the stubs so each test can arrange in-memory state.
/// <para>
/// The database is wiped of routing-relevant state (ApplicationSettings rows
/// for <c>ServiceModes</c>, catalog models, runtime profiles) before each test
/// method so tests do not see each other's writes. Project and notebook rows are
/// preserved, but notebook <c>GuideId</c> links are cleared so assistant cleanup
/// does not hit <c>FK_Notebooks_Assistants_GuideId</c>.
/// </para>
/// </summary>
[TestClass]
public abstract class SettingsRoutingIntegrationTestBase : IAsyncDisposable
{
    private static int _sharedFactoryRefCount;
    private static readonly SemaphoreSlim SharedFactoryGate = new(1, 1);

    protected static SettingsRoutingTestWebApplicationFactory? SharedFactory;
    protected HttpClient Client { get; private set; } = null!;

    public static async Task InitializeSharedFactoryAsync(TestContext context)
    {
        await SharedFactoryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (SharedFactory is null)
            {
                SharedFactory = new SettingsRoutingTestWebApplicationFactory();
                await SharedFactory.InitializeAsync().ConfigureAwait(false);
            }

            Interlocked.Increment(ref _sharedFactoryRefCount);
        }
        finally
        {
            SharedFactoryGate.Release();
        }
    }

    public static async Task DisposeSharedFactoryAsync()
    {
        await SharedFactoryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var remaining = Interlocked.Decrement(ref _sharedFactoryRefCount);
            if (remaining > 0)
            {
                return;
            }

            if (remaining < 0)
            {
                Interlocked.Increment(ref _sharedFactoryRefCount);
                return;
            }

            if (SharedFactory is not null)
            {
                await SharedFactory.DisposeAsync().ConfigureAwait(false);
                SharedFactory = null;
            }
        }
        finally
        {
            SharedFactoryGate.Release();
        }
    }

    protected static StubLlamaServerRuntimeClient LlamaStub =>
        SharedFactory?.LlamaStub ?? throw new InvalidOperationException("SharedFactory not initialized.");

    protected static StubRouterModelsConfigService RouterStub =>
        SharedFactory?.RouterStub ?? throw new InvalidOperationException("SharedFactory not initialized.");

    [TestInitialize]
    public virtual async Task BaseTestInitialize()
    {
        if (SharedFactory == null)
        {
            throw new InvalidOperationException("SharedFactory is not initialized. Ensure ClassInitialize ran properly.");
        }

        Client = SharedFactory.CreateClient();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IntegrationTestAuthTokenFactory.CreateAdminToken());

        await ResetRoutingStateAsync();
        ResetStubs();
    }

    [TestCleanup]
    public virtual Task BaseTestCleanup()
    {
        Client?.Dispose();
        return Task.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync()
    {
        await BaseTestCleanup();
        GC.SuppressFinalize(this);
    }

    protected static void ResetStubs()
    {
        if (SharedFactory == null)
        {
            return;
        }

        SharedFactory.LlamaStub.Reset();
        SharedFactory.RouterStub.Reset();

        using var scope = SharedFactory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMemoryCache>().Remove("llama.runtime.inventory");
    }

    protected static async Task ResetRoutingStateAsync()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ApplicationSettings WHERE SectionName = 'ServiceModes';");

        // Assistants reference Models via ModelId and must be removed before Models.
        // Other test classes share this DB and may leave notebooks / conversation
        // messages that reference Assistants via Restrict FKs — clear those first,
        // but keep project + notebook rows intact.
        await db.Database.ExecuteSqlRawAsync("UPDATE Notebooks SET GuideId = NULL WHERE GuideId IS NOT NULL;");
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE NotebookConversationMessages SET AssistantId = NULL WHERE AssistantId IS NOT NULL;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM GuideMembers;");
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentInvocations SET ParentInvocationId = NULL;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM AgentInvocationMessages;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM AgentInvocations;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Assistants;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Models;");
    }

    /// <summary>
    /// Minimal valid model-owned thinking control for llama-cpp routing/chat dispatch.
    /// ChoiceActions must be non-empty or ChatTargetValidator rejects the model.
    /// </summary>
    protected const string DefaultLlamaThinkingControlJson =
        """{"defaultChoice":"none","choiceActions":{"none":[],"enabled":[]}}""";

    protected static async Task<Model> SeedCatalogModelAsync(
        string modelId,
        string provider,
        string? RuntimeConfigJson = null,
        string displayName = "Test Model",
        bool isActive = true,
        string? ThinkingControlJson = null)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var isLlama = string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase);
        var thinkingControlJson = ThinkingControlJson
            ?? (isLlama ? DefaultLlamaThinkingControlJson : "{}");

        var existing = await db.Models.FirstOrDefaultAsync(m => m.ModelId == modelId);
        if (existing != null)
        {
            existing.Provider = provider;
            existing.RuntimeConfigJson = RuntimeConfigJson;
            existing.IsActive = isActive;
            existing.DisplayName = displayName;
            existing.ThinkingControlJson = thinkingControlJson;
            if (isLlama)
            {
                existing.ReasoningChoicesJson = """["none","enabled"]""";
            }
        }
        else
        {
            existing = new Model
            {
                ModelId = modelId,
                DisplayName = displayName,
                Provider = provider,
                IsActive = isActive,
                RuntimeConfigJson = RuntimeConfigJson,
                ThinkingControlJson = thinkingControlJson,
                ReasoningChoicesJson = isLlama ? """["none","enabled"]""" : null,
            };
            db.Models.Add(existing);
        }

        await db.SaveChangesAsync();
        return existing;
    }
}

