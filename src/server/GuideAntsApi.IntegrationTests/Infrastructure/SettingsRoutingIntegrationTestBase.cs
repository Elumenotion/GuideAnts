using Microsoft.EntityFrameworkCore;
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
    protected static SettingsRoutingTestWebApplicationFactory? SharedFactory;
    protected HttpClient Client { get; private set; } = null!;

    public static async Task InitializeSharedFactoryAsync(TestContext context)
    {
        SharedFactory = new SettingsRoutingTestWebApplicationFactory();
        await SharedFactory.InitializeAsync();
    }

    public static async Task DisposeSharedFactoryAsync()
    {
        if (SharedFactory != null)
        {
            await SharedFactory.DisposeAsync();
            SharedFactory = null;
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
    }

    protected static async Task ResetRoutingStateAsync()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ApplicationSettings WHERE SectionName = 'ServiceModes';");

        // Assistants reference Models via ModelId and must be removed before Models.
        // Other test classes share this DB and may leave notebooks / guides that
        // reference Assistants via Restrict FKs — clear those first, but keep
        // project + notebook rows intact.
        await db.Database.ExecuteSqlRawAsync("UPDATE Notebooks SET GuideId = NULL WHERE GuideId IS NOT NULL;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM GuideMembers;");
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentInvocations SET ParentInvocationId = NULL;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM AgentInvocationMessages;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM AgentInvocations;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Assistants;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM Models;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM RuntimeProfiles;");
    }

    protected static async Task<Model> SeedCatalogModelAsync(
        string modelId,
        string provider,
        string? RuntimeConfigJson = null,
        string displayName = "Test Model",
        bool isActive = true)
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existing = await db.Models.FirstOrDefaultAsync(m => m.ModelId == modelId);
        if (existing != null)
        {
            existing.Provider = provider;
            existing.RuntimeConfigJson = RuntimeConfigJson;
            existing.IsActive = isActive;
            existing.DisplayName = displayName;
        }
        else
        {
            existing = new Model
            {
                ModelId = modelId,
                DisplayName = displayName,
                Provider = provider,
                IsActive = isActive,
                RuntimeConfigJson = RuntimeConfigJson
            };
            db.Models.Add(existing);
        }

        await db.SaveChangesAsync();
        return existing;
    }
}

