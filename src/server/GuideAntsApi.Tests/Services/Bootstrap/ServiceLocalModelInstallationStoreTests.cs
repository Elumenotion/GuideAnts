using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Reachability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.Services.Bootstrap;

/// <summary>T-U6 — install memory: captured API-side at acceptance (F-5, no engine
/// interrogation on the write path); no phantom Installed row; terminal-success
/// enrichment; offline delete removes the row; MarkObservedAsync never touches
/// ServiceModes. No digest/Repository/ResolvedRevision assertions — those columns
/// are gone (plan correction).</summary>
[TestClass]
public sealed class ServiceLocalModelInstallationStoreTests
{
    private static (IServiceLocalModelInstallationStore store, IServiceProvider provider) CreateStore()
    {
        var dbName = $"slmi-{Guid.NewGuid():N}"; // hoisted: one database for every scope in this test
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IServiceLocalModelInstallationStore, ServiceLocalModelInstallationStore>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IServiceLocalModelInstallationStore>(), provider);
    }

    [TestMethod]
    public async Task DownloadAccepted_CreatesRow_WithCapturedCoordinates_NoEngineInterrogation()
    {
        var (store, _) = CreateStore();

        var row = await store.UpsertAsync(
            "SpeechSynthesis", "chatterbox", "model",
            catalogId: "chatterbox", catalogVersion: "2025-08-01", requestedRevision: null,
            state: "Accepted", operationId: "op-123");

        row.ServiceId.Should().Be("SpeechSynthesis");
        row.ModelRef.Should().Be("chatterbox");
        row.ContentKind.Should().Be("model");
        row.CatalogId.Should().Be("chatterbox");
        row.CatalogVersion.Should().Be("2025-08-01");
        row.RequestedRevision.Should().BeNull();
        row.State.Should().Be("Accepted");
        row.LastOperationId.Should().Be("op-123");
        row.ObservedState.Should().BeNull("observation is written only by Plane C");
    }

    [TestMethod]
    public async Task EngineRefused_NoInstalledRow_OnlyFailedOrNothing()
    {
        var (store, _) = CreateStore();
        // Refusal = the download never got accepted; no row exists unless the endpoint
        // recorded one. Marking a nonexistent row failed is a no-op (never invents rows).
        await store.MarkFailedAsync("SpeechSynthesis", "never-accepted", "engine refused");
        (await store.TryGetAsync("SpeechSynthesis", "never-accepted")).Should().BeNull();
    }

    [TestMethod]
    public async Task OperationTerminalSuccess_EnrichesRow_WithArtifactsPathAndSize()
    {
        var (store, _) = CreateStore();
        await store.UpsertAsync("SpeechSynthesis", "chatterbox", "model", "chatterbox", "v1", null, "Accepted", "op-1");

        await store.SetInstalledAsync("SpeechSynthesis", "chatterbox", "/models/chatterbox",
 """[{"path":"/models/chatterbox/model.gguf","sizeBytes":12345}]""");

        var row = await store.TryGetAsync("SpeechSynthesis", "chatterbox");
        row.Should().NotBeNull();
        row!.State.Should().Be("Installed");
        row.TargetDirectory.Should().Be("/models/chatterbox");
        row.ArtifactsJson.Should().Contain("sizeBytes").And.Contain("model.gguf");
        row.ArtifactsJson.Should().NotContain("digest");
    }

    [TestMethod]
    public async Task OfflineDelete_RemovesRow_WithoutAnyPendingArtifact()
    {
        var (store, _) = CreateStore();
        await store.UpsertAsync("SpeechSynthesis", "chatterbox", "model", "chatterbox", null, null, "Installed");

        await store.RemoveAsync("SpeechSynthesis", "chatterbox");

        (await store.TryGetAsync("SpeechSynthesis", "chatterbox")).Should().BeNull();
        (await store.ListForServiceAsync("SpeechSynthesis")).Should().BeEmpty();
    }

    [TestMethod]
    public async Task MarkObservedAsync_RecordsPresence_NeverTouchesStateOrSelection()
    {
        var (store, _) = CreateStore();
        await store.UpsertAsync("Embeddings", "bge", "model", "bge", null, null, "Accepted", "op-2");

        await store.MarkObservedAsync("Embeddings", "bge", present: false, error: null, observedUtc: DateTime.UtcNow);

        var row = await store.TryGetAsync("Embeddings", "bge");
        row!.ObservedState.Should().Be("absent");
        row.ObservedUtc.Should().NotBeNull();
        row.State.Should().Be("Accepted", "observation never mutates the API-owned lifecycle state");
    }

    [TestMethod]
    public async Task MarkObservedAsync_OnMissingRow_IsANoOp_NeverInventsOne()
    {
        var (store, _) = CreateStore();
        await store.MarkObservedAsync("Embeddings", "ghost", present: true, error: null, observedUtc: DateTime.UtcNow);
        (await store.TryGetAsync("Embeddings", "ghost")).Should().BeNull("observation must not fabricate install memory");
    }
}
