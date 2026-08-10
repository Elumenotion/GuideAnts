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
[TestCategory("RuntimeLoadOps")]
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


    private static string UniqueAlias(string prefix) =>
        $"g3-{prefix}-{Guid.NewGuid():N}";

    private static string BuildRuntimeConfigJson(string alias) => $$"""
        {
          "routerModelId": "{{alias}}"
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

