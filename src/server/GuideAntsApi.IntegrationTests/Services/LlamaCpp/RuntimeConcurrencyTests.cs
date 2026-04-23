using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Services.LlamaCpp;

/// <summary>
/// Phase G.3 — R-12 concurrency coverage for the llama runtime load/unload
/// contract. Each test exercises the HTTP surface of
/// <c>/api/settings/llama/runtime/{load,unload,status}</c> together with the
/// <see cref="ILlamaRuntimeCoordinator"/> lock semantics, using a gated
/// <see cref="GuideAntsApi.IntegrationTests.TestUtils.StubLlamaServerRuntimeClient"/>
/// to hold a load call in flight and observe contention deterministically.
/// <para>
/// R-12 requirements covered:
/// <list type="bullet">
///   <item>R-12.1 / R-12.4 — per-alias serialization of load ops.</item>
///   <item>R-12.10 — unload contends with in-flight load on the same alias.</item>
///   <item>R-6.10 / R-12 adjacent — the diagnostic /runtime/status endpoint is
///         non-blocking and reports <c>InProgress=true</c> during a gated load.</item>
///   <item>R-12.2 — the load path does reach the llama client (log entry).</item>
///   <item>R-12.1 (cross-alias) — concurrent loads on distinct aliases run in
///         parallel without blocking each other.</item>
/// </list>
/// The test class is <see cref="DoNotParallelizeAttribute"/>-scoped via MSTest's
/// default behavior (methods inside one class are serialized), so each test has
/// exclusive access to the shared factory's coordinator + stub state.
/// </para>
/// </summary>
[TestClass]
public sealed class RuntimeConcurrencyTests : SettingsRoutingIntegrationTestBase
{
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

    /// <summary>
    /// R-12.1 + R-12.4 + R-12.10: a second load request for the same alias
    /// while the first is in flight MUST surface a deterministic 409 with the
    /// <c>ROUTING_RUNTIME_NOT_READY</c> code and a remediation action, rather
    /// than silently stalling or interleaving.
    /// </summary>
    [TestMethod]
    public async Task Load_SameAlias_Concurrent_SecondCallReturns409RuntimeBusy()
    {
        var alias = UniqueAlias("serialize");
        SeedAliasArtifacts(alias);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaStub.SetLoadGate(alias, gate);

        var loadPayload = new LlamaRuntimeLoadRequest(alias);

        var firstLoadTask = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", loadPayload);
        await WaitUntilAliasLockedAsync(alias);

        var secondLoadResponse = await Client.PostAsJsonAsync("/api/settings/llama/runtime/load", loadPayload);

        secondLoadResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "R-12.4 — the alias lock must reject a concurrent second load with 409.");
        secondLoadResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using (var doc = JsonDocument.Parse(await secondLoadResponse.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("status").GetInt32().Should().Be(409);
            doc.RootElement.GetProperty("code").GetString().Should().Be(RoutingErrorCodes.RuntimeNotReady);
            doc.RootElement.GetProperty("action").GetString().Should().NotBeNullOrWhiteSpace(
                "R-2.2 — every ROUTING_* ProblemDetails must carry a remediation action.");
            doc.RootElement.GetProperty("modelId").GetString().Should().Be(alias);
        }

        gate.TrySetResult();
        var firstResponse = await firstLoadTask;
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "R-12.2 — once the gated load completes, the first call succeeds and hits the runtime client.");

        LlamaStub.LoadLog.Should().Contain(alias,
            "R-12.2 — the load path must reach the llama server runtime client.");
    }

    /// <summary>
    /// R-12.1 (cross-alias variant): loads targeting distinct aliases must not
    /// contend with each other. This is the non-trivial shape of the lock
    /// contract — serialization is per-alias, not global.
    /// </summary>
    [TestMethod]
    public async Task Load_DifferentAliases_Concurrent_BothProceedInParallel()
    {
        var aliasA = UniqueAlias("parallelA");
        var aliasB = UniqueAlias("parallelB");
        SeedAliasArtifacts(aliasA);
        SeedAliasArtifacts(aliasB);

        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaStub.SetLoadGate(aliasA, gateA);
        LlamaStub.SetLoadGate(aliasB, gateB);

        var taskA = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", new LlamaRuntimeLoadRequest(aliasA));
        var taskB = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", new LlamaRuntimeLoadRequest(aliasB));

        // Both loads must be in flight simultaneously. We observe this via the
        // coordinator rather than the stub so the assertion exercises the
        // production per-alias lock map, not the stub's internal state.
        await WaitUntilAliasLockedAsync(aliasA);
        await WaitUntilAliasLockedAsync(aliasB);

        var coordinator = SharedFactory!.Services.GetRequiredService<ILlamaRuntimeCoordinator>();
        coordinator.IsAliasLocked(aliasA).Should().BeTrue();
        coordinator.IsAliasLocked(aliasB).Should().BeTrue(
            "R-12.1 — distinct aliases must not share a lock.");

        gateA.TrySetResult();
        gateB.TrySetResult();

        (await taskA).StatusCode.Should().Be(HttpStatusCode.OK);
        (await taskB).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// R-12.10: an unload operation targeting an alias that is the subject of
    /// an in-flight load operation MUST be rejected deterministically with a
    /// 409 rather than interleaving with the load and leaving the runtime in
    /// an indeterminate state.
    /// </summary>
    [TestMethod]
    public async Task Unload_DuringLoad_ReturnsDeterministic409()
    {
        var alias = UniqueAlias("unloadContention");
        SeedAliasArtifacts(alias);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaStub.SetLoadGate(alias, gate);

        var loadTask = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", new LlamaRuntimeLoadRequest(alias));
        await WaitUntilAliasLockedAsync(alias);

        var unloadResponse = await Client.PostAsJsonAsync(
            "/api/settings/llama/runtime/unload",
            new LlamaRuntimeUnloadRequest(alias));

        unloadResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "R-12.10 — the settings UI unload must not silently tear down an alias with an in-flight load.");

        using (var doc = JsonDocument.Parse(await unloadResponse.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("code").GetString().Should().Be(RoutingErrorCodes.RuntimeNotReady);
            doc.RootElement.GetProperty("action").GetString().Should().NotBeNullOrWhiteSpace();
        }

        LlamaStub.UnloadLog.Should().NotContain(alias,
            "R-12.10 — contention must block, never interleave.");

        gate.TrySetResult();
        (await loadTask).StatusCode.Should().Be(HttpStatusCode.OK);

        // After the load settles the lock is released, so an unload can now
        // proceed in a fresh, deterministic state.
        var secondUnload = await Client.PostAsJsonAsync(
            "/api/settings/llama/runtime/unload",
            new LlamaRuntimeUnloadRequest(alias));
        secondUnload.StatusCode.Should().Be(HttpStatusCode.OK);
        LlamaStub.UnloadLog.Should().Contain(alias);
    }

    /// <summary>
    /// R-6.10 / R-12 adjacent: the diagnostic status endpoint must NOT block
    /// on the alias lock. Reading status during an in-flight load is the way
    /// the UI renders the "in progress" pill, so it must return immediately
    /// with <c>InProgress=true</c>.
    /// </summary>
    [TestMethod]
    public async Task RuntimeStatus_IsNonBlocking_ReportsInProgressDuringLoad()
    {
        var alias = UniqueAlias("statusReadDuringLoad");
        SeedAliasArtifacts(alias);
        LlamaStub.SeedState(alias, "loading");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaStub.SetLoadGate(alias, gate);

        var loadTask = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", new LlamaRuntimeLoadRequest(alias));
        await WaitUntilAliasLockedAsync(alias);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var statusResponse = await Client.GetAsync("/api/settings/llama/runtime/status");
        sw.Stop();

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "R-6.10 — the diagnostic status read must not block on the alias lock; " +
            "a value this high implies the status handler is acquiring the lock instead of sampling it.");

        var statuses = await statusResponse.Content.ReadFromJsonAsync<List<LlamaRuntimeAliasStatusDto>>();
        statuses.Should().NotBeNull();
        var aliasStatus = statuses!.FirstOrDefault(s => s.Alias == alias);
        aliasStatus.Should().NotBeNull($"alias '{alias}' should appear in the status list while the load is gated.");
        aliasStatus!.InProgress.Should().BeTrue(
            "R-6.10 — a gated load alias must report InProgress=true via the lock snapshot or 'loading' runtime state.");

        gate.TrySetResult();
        (await loadTask).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// R-12 compliance note: <see cref="IRoutingReadinessService.ProbeChatTargetAsync"/>
    /// is a point-in-time snapshot, not a long-poll. While a load is in flight
    /// for a llama-cpp chat target, the readiness endpoint MUST deterministically
    /// report the snapshot — today that surfaces <c>RUNTIME_STATE</c> with the
    /// current state (<c>loading</c> or <c>unloaded</c>) and a 409. The chat
    /// dispatch path (<see cref="GuideAntsApi.Services.LlamaCpp.NotebookModelRuntimeService"/>)
    /// is the piece that awaits orchestration before invoking the validator
    /// (R-12.5); this test pins the snapshot contract so any future change
    /// that tries to silently wait inside the readiness probe is caught.
    /// </summary>
    [TestMethod]
    public async Task ChatTargetReadiness_DuringInFlightLoad_ReportsLoadingStateBlocker()
    {
        var alias = UniqueAlias("chatDuringLoad");
        var modelId = "chat-" + alias.ToLowerInvariant();

        SeedAliasArtifacts(alias);
        await SeedCatalogModelAsync(modelId, "llama-cpp",
            localRuntimeJson: BuildLocalRuntimeJson(alias));

        LlamaStub.SeedState(alias, "loading");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaStub.SetLoadGate(alias, gate);

        var loadTask = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", new LlamaRuntimeLoadRequest(alias));
        await WaitUntilAliasLockedAsync(alias);

        var readiness = await Client.GetAsync(
            $"/api/settings/routing/chat-targets/{modelId}/readiness?strict=true");

        readiness.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "readiness is a snapshot — a loading alias is reported as a RUNTIME_STATE blocker.");

        using (var doc = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("code").GetString().Should().Be(RoutingErrorCodes.ModelNotReady);
            var blockers = doc.RootElement.GetProperty("blockers").EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .ToList();
            blockers.Should().Contain(b => b.StartsWith("RUNTIME_STATE:", StringComparison.Ordinal),
                "the blocker list must cite the underlying RUNTIME_STATE reason, not silently substitute another provider.");
        }

        gate.TrySetResult();
        (await loadTask).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Phase H.2 (R-12.5): the chat dispatch path — <c>IChatCompletionClientFactory.CreateClient</c>
    /// wired to <see cref="GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory"/> —
    /// must NOT fail spuriously with <c>ROUTING_RUNTIME_NOT_READY</c> while a
    /// load is in flight for the backing llama alias. The snapshot behavior
    /// (the adjacent <c>ChatTargetReadiness_DuringInFlightLoad_ReportsLoadingStateBlocker</c>
    /// test) is for the readiness probe only; chat dispatch validates static
    /// shape (provider, LocalRuntimeJson, runtime profile) and lets
    /// <see cref="GuideAntsApi.Services.LlamaCpp.NotebookModelRuntimeService"/>
    /// orchestrate the load lifecycle.
    /// </summary>
    [TestMethod]
    public async Task ChatDispatch_DuringInFlightLoad_DoesNotFailWithRoutingRuntimeNotReady()
    {
        var alias = UniqueAlias("chatDispatchDuringLoad");
        var modelId = "chat-dispatch-" + alias.ToLowerInvariant();

        SeedAliasArtifacts(alias);
        await EnsureConcurrencyRuntimeProfileAsync();
        await SeedCatalogModelAsync(modelId, "llama-cpp",
            localRuntimeJson: BuildLocalRuntimeJson(alias));

        LlamaStub.SeedState(alias, "loading");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaStub.SetLoadGate(alias, gate);

        var loadTask = Client.PostAsJsonAsync("/api/settings/llama/runtime/load", new LlamaRuntimeLoadRequest(alias));
        await WaitUntilAliasLockedAsync(alias);

        // While the alias is mid-load, run the chat dispatch path exactly as a
        // real chat request would: resolver → validator → provider client
        // creation. The production DI graph returns RoutingChatCompletionClientFactory.
        var chatFactory = SharedFactory!.Services
            .GetRequiredService<AntRunner.Chat.Abstractions.IChatCompletionClientFactory>();

        Exception? captured = null;
        AntRunner.Chat.Abstractions.IChatCompletionClient? client = null;
        try
        {
            client = chatFactory.CreateClient(modelId);
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        if (captured is RoutingException routing)
        {
            routing.Code.Should().NotBe(
                RoutingErrorCodes.RuntimeNotReady,
                "R-12.5 — chat dispatch must not surface ROUTING_RUNTIME_NOT_READY for an alias that is mid-load. " +
                "Live load state is inspected by the readiness snapshot, not by the validator.");
        }

        captured.Should().BeNull(
            "R-12.5 — with a well-formed catalog model, local runtime JSON, and runtime profile, " +
            "chat dispatch validates static shape and succeeds regardless of live alias load state.");
        client.Should().NotBeNull();
        client.Should().BeOfType<AntRunner.Chat.LlamaCpp.LlamaCppChatClient>();

        gate.TrySetResult();
        (await loadTask).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Ensures the "concurrency-profile" runtime profile exists. Used by the
    /// chat-dispatch-during-load test so the validator's runtime profile
    /// resolution succeeds and we exercise the full dispatch path. Idempotent:
    /// silently returns if another test in the run already created it.
    /// </summary>
    private async Task EnsureConcurrencyRuntimeProfileAsync()
    {
        // Profile ids are validated against [A-Za-z0-9_] only (see
        // ApplicationSettingsService.ValidateProfileId) — hyphens produce a
        // 400, not a 409, so use underscores here.
        var existing = await Client.GetAsync("/api/settings/runtime-profiles/concurrency_profile");
        if (existing.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        var profileRequest = new CreateRuntimeProfileRequest(
            ProfileId: "concurrency_profile",
            DisplayName: "Concurrency Test Profile",
            Description: "Used by RuntimeConcurrencyTests to satisfy runtime profile resolution.",
            CombineSystemAndDeveloperMessages: true,
            ThoughtBlockPattern: null,
            SamplingParametersJson: "{}",
            // ValidateRuntimeProfileJson requires defaultChoice to be a key in
            // choiceActions when it is non-empty; also ToLlamaCppProfileData calls
            // ToDictionary on ChoiceActions without a null-check. Supply a single
            // no-op "disabled" entry to satisfy both.
            ThinkingControlJson: "{\"defaultChoice\":\"disabled\",\"choiceActions\":{\"disabled\":[]}}");

        var response = await Client.PostAsJsonAsync("/api/settings/runtime-profiles", profileRequest);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Conflict);
    }

    private static string UniqueAlias(string prefix) =>
        $"g3-{prefix}-{Guid.NewGuid():N}";

    private static string BuildLocalRuntimeJson(string alias) => $$"""
        {
          "routerModelId": "{{alias}}",
          "resourceGroupKey": "local",
          "runtimeProfileId": "concurrency_profile"
        }
        """;

    private static void SeedAliasArtifacts(string alias)
    {
        var modelContainerPath = $"/models-local/llama/{alias}/{alias}.gguf";
        var mmprojContainerPath = $"/models-local/llama/{alias}/mmproj.gguf";

        RouterStub.SeedEntry(alias, modelContainerPath, mmprojContainerPath);
    }

    /// <summary>
    /// Polls <see cref="ILlamaRuntimeCoordinator.IsAliasLocked"/> until the
    /// alias lock has been acquired by the in-flight load call. The load
    /// request is issued via HTTP, so the lock acquisition happens on the
    /// server thread and we need a bounded wait here to hand off cleanly.
    /// </summary>
    private static async Task WaitUntilAliasLockedAsync(string alias, int timeoutMs = 5000)
    {
        var coordinator = SharedFactory!.Services.GetRequiredService<ILlamaRuntimeCoordinator>();
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (coordinator.IsAliasLocked(alias))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Alias '{alias}' was not locked by the in-flight load within {timeoutMs}ms. " +
            "Either the POST /runtime/load handler failed to reach the coordinator, or the stub gate fired too early.");
    }
}
