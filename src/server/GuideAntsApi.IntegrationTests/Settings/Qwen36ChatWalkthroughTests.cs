using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Settings;

/// <summary>
/// Phase G.2 — R-8 (§9) walkthrough.
/// <para>
/// Mirrors the Qwen3.6 onboarding sequence described in
/// <c>docs/settings-and-llama-completion-requirements.md</c>:
/// <list type="number">
///   <item>Bootstrap with an empty catalog / empty router-models.ini.</item>
///   <item>Seed the Qwen3.6 runtime profile + catalog row.</item>
///   <item>Seed the router alias (stand-in for a HuggingFace download completing).</item>
///   <item>Load the alias via <c>POST /api/settings/llama/runtime/load</c>.</item>
///   <item>Dispatch a chat through the production
///         <see cref="IChatCompletionClientFactory"/> (the real
///         <c>RoutingChatCompletionClientFactory</c>).</item>
///   <item>Assert R-12.6 behavior — the validator does not fail merely because
///         the runtime is momentarily unloaded at validator time.</item>
///   <item>Cover the failure modes: unknown model, blank provider, missing
///         LocalRuntimeJson.</item>
/// </list>
/// No notebook / guide / crew is seeded here — chat dispatch in the routing
/// factory is assistant-driven but only reads <see cref="Model"/> and
/// <see cref="RuntimeProfile"/>. Load orchestration (per-notebook readiness,
/// crew-wide required-model sets) is exercised by the existing
/// <c>NotebookModelRuntimeServiceTests</c> suite and is not duplicated here.
/// </para>
/// </summary>
[TestClass]
public sealed class Qwen36ChatWalkthroughTests : SettingsRoutingIntegrationTestBase
{
    private const string CatalogModelId = "qwen3.6-35b-a3b-local";
    private const string RouterAlias = "qwen3.6-35b-a3b";
    private const string RuntimeProfileId = "qwen3_6";

    private static readonly string LocalRuntimeJson = JsonSerializer.Serialize(new
    {
        routerModelId = RouterAlias,
        runtimeProfileId = RuntimeProfileId,
        parallelToolCalls = true
    });

    [ClassInitialize]
    public static async Task ClassInit(TestContext ctx)
    {
        await InitializeSharedFactoryAsync(ctx);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
    }

    [TestMethod]
    public async Task Bootstrap_Empty_InventoryReturnsEmpty()
    {
        // R-8 step 1: fresh install — no catalog rows, no router aliases.
        var response = await Client.GetAsync("/api/settings/llama/runtime/inventory");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<LlamaRuntimeInventoryItemDto>>();
        items.Should().NotBeNull();
        items!.Should().BeEmpty(
            "ResetRoutingStateAsync wipes Models + RuntimeProfiles and ResetStubs clears the router stub; " +
            "inventory is the union of catalog + router aliases and both sides are empty.");
    }

    [TestMethod]
    public async Task AddCatalogAndRouterAlias_InventoryReportsAlias_Unloaded()
    {
        // R-8 steps 2+3: seed the catalog row, runtime profile and router alias.
        await SeedQwenRuntimeProfileAsync();
        await SeedQwenCatalogAsync();
        SeedRouterAliasWithArtifacts();

        var response = await Client.GetAsync("/api/settings/llama/runtime/inventory");
        var items = await response.Content.ReadFromJsonAsync<List<LlamaRuntimeInventoryItemDto>>();
        items.Should().NotBeNull();

        var qwen = items!.SingleOrDefault(i => i.RouterModelId == RouterAlias);
        qwen.Should().NotBeNull("the seeded router alias must surface in the inventory");
        qwen!.HasModelFile.Should().BeTrue(
            "router stub supplies model paths; inventory treats non-empty paths as present when HasModelFile is unset");
        qwen.RuntimeState.Should().Be("unloaded",
            "StubLlamaServerRuntimeClient has not been asked to load this alias yet");
        qwen.CatalogModelIds.Should().Contain(CatalogModelId,
            "LocalRuntimeJson.routerModelId MUST tie the catalog row back to the router alias");
    }

    [TestMethod]
    public async Task LoadAlias_TransitionsToLoaded_ViaSettingsEndpoint()
    {
        // R-8 step 4: the settings /runtime/load endpoint drives the coordinator
        // + stub client. We observe the state transition via the inventory
        // endpoint rather than reaching into the stub directly so this stays an
        // end-to-end HTTP assertion.
        await SeedQwenRuntimeProfileAsync();
        await SeedQwenCatalogAsync();
        SeedRouterAliasWithArtifacts();

        var load = await Client.PostAsJsonAsync(
            "/api/settings/llama/runtime/load",
            new { RouterModelId = RouterAlias });
        load.StatusCode.Should().Be(HttpStatusCode.OK);

        var inventoryResponse = await Client.GetAsync("/api/settings/llama/runtime/inventory");
        var items = await inventoryResponse.Content.ReadFromJsonAsync<List<LlamaRuntimeInventoryItemDto>>();
        items!.Single(i => i.RouterModelId == RouterAlias).RuntimeState
            .Should().Be("loaded");
    }

    [TestMethod]
    public async Task DispatchChat_WithLoadedRuntime_ReturnsLlamaCppClient()
    {
        // R-8 step 5: production routing factory creates a LlamaCpp client
        // without throwing when the full catalog + runtime-profile + router
        // alias + loaded state is in place. The client itself does not make a
        // network call at construction time (validated by inspection of
        // LlamaCppChatClientFactory.CreateClientForProfile).
        await SeedQwenRuntimeProfileAsync();
        await SeedQwenCatalogAsync();
        SeedRouterAliasWithArtifacts();
        LlamaStub.SeedState(RouterAlias, "loaded");

        using var scope = SharedFactory!.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IChatCompletionClientFactory>();

        var client = factory.CreateClient(CatalogModelId);
        client.Should().NotBeNull(
            "resolver + validator should accept a fully-configured Qwen3.6 target");
    }

    [TestMethod]
    public async Task DispatchChat_UnloadedRuntime_StillBuildsClient_R12_6()
    {
        // R-12.6: the validator MUST NOT emit ROUTING_RUNTIME_NOT_READY simply
        // because the alias happens to be unloaded at the instant it runs. That
        // state is an orchestration concern, not a per-turn validator concern.
        // We verify this by leaving the stub in its default "unloaded" state
        // and confirming client construction still succeeds.
        await SeedQwenRuntimeProfileAsync();
        await SeedQwenCatalogAsync();
        SeedRouterAliasWithArtifacts();

        using var scope = SharedFactory!.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IChatCompletionClientFactory>();

        // NOTE: intentionally no SeedState("loaded") here.
        var act = () => factory.CreateClient(CatalogModelId);
        act.Should().NotThrow(
            "R-12.6: validator defers runtime-readiness to notebook load orchestration; " +
            "it may not emit ROUTING_RUNTIME_NOT_READY from a snapshot of the alias state.");
    }

    [TestMethod]
    public void DispatchChat_UnknownModel_ThrowsModelNotReady()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IChatCompletionClientFactory>();

        var act = () => factory.CreateClient("does-not-exist-in-catalog");
        act.Should()
            .Throw<RoutingException>()
            .Where(e => e.Code == RoutingErrorCodes.ModelNotReady,
                "R-9.1: chat resolver fails fast — no silent fallback. Missing catalog row maps to ROUTING_MODEL_NOT_READY.")
            .Which.Action.Should().NotBeNullOrWhiteSpace("R-2.2 requires a non-empty human-readable action");
    }

    [TestMethod]
    public async Task DispatchChat_BlankProvider_ThrowsProviderNotReady()
    {
        // A catalog row with an empty provider reaches the resolver and trips the
        // second-layer guard (provider required before any validator runs).
        await SeedCatalogModelAsync(CatalogModelId, provider: string.Empty, localRuntimeJson: LocalRuntimeJson);

        using var scope = SharedFactory!.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IChatCompletionClientFactory>();

        var act = () => factory.CreateClient(CatalogModelId);
        act.Should()
            .Throw<RoutingException>()
            .Where(e => e.Code == RoutingErrorCodes.ProviderNotReady)
            .Which.Action.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task DispatchChat_LlamaCppWithoutLocalRuntimeJson_ThrowsModelNotReady()
    {
        // R-3.1.(c): an llama-cpp catalog row without LocalRuntimeJson cannot be
        // dispatched. The validator must emit ROUTING_MODEL_NOT_READY.
        await SeedCatalogModelAsync(CatalogModelId, provider: "llama-cpp", localRuntimeJson: null);

        using var scope = SharedFactory!.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IChatCompletionClientFactory>();

        var act = () => factory.CreateClient(CatalogModelId);
        act.Should()
            .Throw<RoutingException>()
            .Where(e => e.Code == RoutingErrorCodes.ModelNotReady);
    }

    [TestMethod]
    public async Task UnloadAfterLoad_FollowedByChat_StillBuildsClient()
    {
        // R-12.6 restated from the other direction: unloading an alias in
        // Settings MUST NOT break chat dispatch — orchestration at chat time
        // will re-load on demand. The factory itself never peeks at runtime
        // state, so the unload does not cause ROUTING_RUNTIME_NOT_READY at
        // validator time.
        await SeedQwenRuntimeProfileAsync();
        await SeedQwenCatalogAsync();
        SeedRouterAliasWithArtifacts();

        var load = await Client.PostAsJsonAsync(
            "/api/settings/llama/runtime/load",
            new { RouterModelId = RouterAlias });
        load.StatusCode.Should().Be(HttpStatusCode.OK);

        var unload = await Client.PostAsJsonAsync(
            "/api/settings/llama/runtime/unload",
            new { RouterModelId = RouterAlias });
        unload.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = SharedFactory!.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IChatCompletionClientFactory>();
        factory.CreateClient(CatalogModelId).Should().NotBeNull();
    }

    // ---------------- helpers ----------------

    private static async Task SeedQwenRuntimeProfileAsync()
    {
        using var scope = SharedFactory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = new RuntimeProfile
        {
            ProfileId = RuntimeProfileId,
            DisplayName = "Qwen3.6",
            Description = "Phase G.2 test runtime profile",
            CombineSystemAndDeveloperMessages = true,
            ThoughtBlockPattern = @"<think>[\s\S]*?</think>",
            SamplingParametersJson = "{}",
            ThinkingControlJson = "{\"defaultChoice\":\"enabled\",\"choiceActions\":{}}"
        };
        db.RuntimeProfiles.Add(profile);
        await db.SaveChangesAsync();

        // Bust the resolver cache so the newly-seeded row is visible to the
        // next Resolve() call. The resolver is a singleton with a 5-minute TTL
        // so without this the second test in a class re-uses the stale entry.
        var resolver = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.LlamaCpp.IRuntimeProfileResolver>();
        resolver.InvalidateCache();
    }

    private static Task SeedQwenCatalogAsync() =>
        SeedCatalogModelAsync(
            modelId: CatalogModelId,
            provider: "llama-cpp",
            localRuntimeJson: LocalRuntimeJson,
            displayName: "Qwen3.6 35B A3B (local)");

    private static void SeedRouterAliasWithArtifacts()
    {
        var modelRel = $"{RouterAlias}/{RouterAlias}.gguf";
        var mmprojRel = $"{RouterAlias}/mmproj.gguf";
        RouterStub.SeedEntry(RouterAlias, modelRel, mmprojRel);
    }
}
