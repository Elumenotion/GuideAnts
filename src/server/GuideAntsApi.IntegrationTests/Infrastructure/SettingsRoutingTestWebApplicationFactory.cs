using AntRunner.Chat.Abstractions;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

using GuideAntsApi.IntegrationTests.TestUtils;

/// <summary>
/// Phase G settings/routing factory. Extends the shared
/// <see cref="TestWebApplicationFactory"/> with in-memory stubs for the llama
/// runtime (HTTP client) and router-models.ini service so the settings +
/// routing endpoints can be exercised without a real llama-server container.
/// <para>
/// The fake chat completion factory installed by the base factory is removed
/// here — several Phase G tests (notably the Qwen3.6 walkthrough and the
/// RuntimeConcurrency suite) rely on the production
/// <c>RoutingChatCompletionClientFactory</c> being resolvable from DI so the
/// chat resolver + validator chain can be observed end-to-end.
/// </para>
/// </summary>
public sealed class SettingsRoutingTestWebApplicationFactory : TestWebApplicationFactory
{
    public StubLlamaServerRuntimeClient LlamaStub { get; } = new();
    public StubRouterModelsConfigService RouterStub { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILlamaServerRuntimeClient>();
            services.AddSingleton<ILlamaServerRuntimeClient>(LlamaStub);

            services.RemoveAll<IRouterModelsConfigService>();
            services.AddSingleton<IRouterModelsConfigService>(RouterStub);

            services.RemoveAll<ILocalAiStartupWarmupService>();
            services.AddSingleton<ILocalAiStartupWarmupService>(_ => new StubLocalAiWarmupService(LlamaStub));

            // Restore production chat factory so chat resolver + validator
            // chain is observable in Phase G tests. The base factory installs
            // a fake to keep unrelated endpoint tests deterministic; we need
            // the real one here.
            services.RemoveAll<IChatCompletionClientFactory>();
            services.AddSingleton<IChatCompletionClientFactory, GuideAntsApi.Services.Conversations.RoutingChatCompletionClientFactory>();
        });
    }

    /// <summary>
    /// Stand-in for ga-admin warmup apply. Settings <c>/runtime/load</c> now
    /// routes through <see cref="ILocalAiWarmupService.SyncDesiredAndApplyAsync"/>;
    /// for Phase G HTTP tests we delegate llama alias loads to
    /// <see cref="StubLlamaServerRuntimeClient"/> so inventory/state assertions
    /// and R-12 concurrency gates still observe the runtime client.
    /// </summary>
    private sealed class StubLocalAiWarmupService(StubLlamaServerRuntimeClient llamaStub)
        : ILocalAiStartupWarmupService, ILocalAiWarmupService
    {
        public bool IsWarmupInProgress => false;

        public bool IsApplyInProgress => false;

        public Task WarmupAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureDefaultLlamaLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureAuxiliaryServicesLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UnloadAuxiliaryServicesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<LocalServiceReconcileResult> ReconcileLocalServiceAsync(
            string serviceId,
            string? requestedModelRef = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Warm));

        public Task<LocalServiceReconcileResult> PowerOffLocalServiceEngineAsync(
            string serviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalServiceReconcileResult(LocalServiceReconcileOutcome.Idle));

        public Task RecycleSharedSpeechEnginesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task SyncDesiredAndApplyAsync(
            WarmupDesiredBuildOptions? options = null,
            bool waitForCompletion = false,
            CancellationToken cancellationToken = default)
        {
            var alias = options?.LlamaRouterAliasOverride?.Trim();
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            await llamaStub.LoadModelAsync(alias, cancellationToken).ConfigureAwait(false);
        }

        public Task<WarmupStatusDocument> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WarmupStatusDocument(
                SchemaVersion: 1,
                DesiredRevision: 0,
                AppliedRevision: 0,
                InProgressRevision: null,
                ApplyStatus: "idle",
                ApplyError: null,
                DesiredSha256: string.Empty,
                WrittenAt: string.Empty,
                Services: new Dictionary<string, WarmupServiceStatus>()));
    }
}
