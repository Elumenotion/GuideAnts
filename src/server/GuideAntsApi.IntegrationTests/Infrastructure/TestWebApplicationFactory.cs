using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.DataModel;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.IntegrationTests.TestUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using AntRunner.Chat.Abstractions;

namespace GuideAntsApi.IntegrationTests.Infrastructure;

public class TestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private const string ConnectionStringEnvColon = "ConnectionStrings:DefaultConnection";
    private const string ConnectionStringEnvDoubleUnderscore = "ConnectionStrings__DefaultConnection";

    private bool _initialized = false;
    private string? _connectionString;
    private string? _priorConnectionStringColon;
    private string? _priorConnectionStringDoubleUnderscore;

    public async Task InitializeAsync()
    {
        if (!_initialized)
        {
            await TestContainerManager.Instance.EnsureInitializedAsync();
            _connectionString = await TestContainerManager.Instance.GetConnectionStringAsync();
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                throw new InvalidOperationException("Test SQL connection string was not initialized.");
            }
            _initialized = true;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "TestWebApplicationFactory was not initialized with a SQL connection string. " +
                "Call InitializeAsync() before creating clients.");
        }

        // Force test container connection string to win in every config access path.
        // Capture priors so DisposeAsync can restore and not leak into unit-test
        // processes that share the VS Test host (DatabaseStorage reads the colon key).
        _priorConnectionStringColon ??= Environment.GetEnvironmentVariable(ConnectionStringEnvColon);
        _priorConnectionStringDoubleUnderscore ??= Environment.GetEnvironmentVariable(ConnectionStringEnvDoubleUnderscore);
        Environment.SetEnvironmentVariable(ConnectionStringEnvDoubleUnderscore, _connectionString);
        Environment.SetEnvironmentVariable(ConnectionStringEnvColon, _connectionString);

        builder.ConfigureAppConfiguration((context, config) =>
        {
            var testProjectDir = GetTestProjectDirectory();
            var configPath = Path.GetFullPath(Path.Combine(testProjectDir, "..", "GuideAntsApi"));

            config.AddJsonFile(Path.Combine(configPath, "appsettings.json"), optional: false, reloadOnChange: true);
            config.AddJsonFile(Path.Combine(configPath, $"appsettings.{context.HostingEnvironment.EnvironmentName}.json"), optional: true, reloadOnChange: true);
            
            // Add test-specific configuration to reduce logging noise
            config.AddJsonFile(Path.Combine(testProjectDir, "appsettings.test.json"), optional: true, reloadOnChange: true);
            
            config.AddEnvironmentVariables();
            
            // Allow derived classes to add their own config before the final in-memory collection
            ConfigureTestAppConfiguration(config, context);

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["KernelMemory:BaseUrl"] = "http://localhost:9001",
                ["NOTEBOOK_TEMPLATES_BASE_FOLDER_PATH"] = Path.GetFullPath(Path.Combine(testProjectDir, "..", "NotebookTemplates")),
                ["Jwt:Issuer"] = IntegrationTestAuthHandler.JwtIssuer,
                ["Jwt:Audience"] = IntegrationTestAuthHandler.JwtAudience,
                ["Jwt:LifetimeMinutes"] = IntegrationTestAuthHandler.JwtLifetimeMinutes.ToString(),
                ["Jwt:SigningKey"] = IntegrationTestAuthHandler.JwtSigningKey,
                ["SandboxWireApi:Issuer"] = "GuideAnts.Test",
                ["SandboxWireApi:Audience"] = "GuideAnts.SandboxWire",
                ["SandboxWireApi:SigningKey"] = "guideants-integration-tests-sandbox-wire-signing-key-2026",
                ["SandboxWireApi:InternalBaseUrl"] = "http://localhost/api/internal/sandbox/openai/v1"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove the existing db context registration
            var descriptor = services.SingleOrDefault(d => 
                d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));

            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            // Add DB context using container connection
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(_connectionString), 
                contextLifetime: ServiceLifetime.Scoped, 
                optionsLifetime: ServiceLifetime.Singleton);

            // No background stale-turn recovery sweep in the test host.
            //
            // The API registers ConversationTurnRecoveryService as a hosted service that sweeps the
            // database every second (StartupConfiguration.cs, AddHostedService<ConversationTurnRecoveryService>).
            // Every test class here stands up its own host against ONE shared Testcontainers database that
            // BaseIntegrationTest.BaseTestInitialize wipes before each test, so a live sweep from any
            // still-running host claims and rewrites other tests' in-flight turns: it terminalizes
            // 'streaming' turns older than its 3-minute cutoff, honors 'cancel_requested' markers left
            // behind by earlier Stop tests, and calls the destructive PruneIncompleteToolCallsAsync. Its
            // batch is capped and ordered (OrderBy(LastUpdated).Take(20)), so which rows it reaches shifts
            // with suite load - the mechanism behind order-dependent, 'fails once then passes' behaviour.
            //
            // ConversationTurnRecoverySqlIntegrationTests constructs the service and calls
            // RecoverStaleTurnsAsync explicitly, so recovery itself stays under test; only the
            // unsupervised background actor is removed.
            // AddHostedService<T> stores T in ImplementationFactory as a compiled lambda, so the type is
            // not readable off the descriptor. Enumerate every IHostedService registration, build it
            // against a throwaway provider, and remove the one that materializes as the recovery service.
            var recoveryType = typeof(GuideAntsApi.Services.Conversations.Recovery.ConversationTurnRecoveryService);
            using (var probeProvider = services.BuildServiceProvider())
            {
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    var candidate = services[i];
                    if (!typeof(IHostedService).IsAssignableFrom(candidate.ServiceType)
                        || candidate.ServiceType != typeof(IHostedService))
                    {
                        continue;
                    }

                    Type? concreteType = candidate.ImplementationType;
                    if (concreteType == null)
                    {
                        try
                        {
                            concreteType = candidate.ImplementationInstance?.GetType()
                                ?? candidate.ImplementationFactory?.Invoke(probeProvider).GetType();
                        }
                        catch
                        {
                            // A factory that cannot run in this probe is left in place; the guard below
                            // catches the case where that means the sweep survived.
                            continue;
                        }
                    }

                    if (concreteType == recoveryType)
                    {
                        services.RemoveAt(i);
                    }
                }
            }

            if (HasRecoveryHostedService(services))
            {
                throw new InvalidOperationException(
                    "TestWebApplicationFactory failed to remove the ConversationTurnRecoveryService hosted service. " +
                    "Do not run the integration suite with an unsupervised 1s stale-turn sweep writing to the " +
                    "shared test database.");
            }

            // Integration tests should not depend on external LLM providers.
            services.RemoveAll<IChatCompletionClientFactory>();
            services.AddSingleton<IChatCompletionClientFactory, FakeChatCompletionClientFactory>();

            // Integration tests should not block on local AI service
            // load/unload polling when calling settings endpoints.
            services.RemoveAll<ILocalServiceLoadService>();
            services.AddSingleton<ILocalServiceLoadService, NoOpLocalServiceLoadService>();

            // appsettings.test.json points LlamaCpp / LocalServiceHosts at localhost:8110.
            // The real admin + warmup orchestration HttpClients use a 4-hour timeout; when
            // something accepts TCP but never answers, overview / llama auth-gate tests hang
            // until the test HttpClient aborts (~100s). Stub both to keep the suite hermetic.
            services.RemoveAll<ILlamaRuntimeAdminClient>();
            services.AddSingleton<ILlamaRuntimeAdminClient, StubLlamaRuntimeAdminClient>();

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IntegrationTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = IntegrationTestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, IntegrationTestAuthHandler>(
                    IntegrationTestAuthHandler.SchemeName,
                    _ => { });

            // The production JwtTokenService captures an immutable JwtOptions snapshot at
            // registration time, before this factory's in-memory config override is applied.
            // Re-register it with the test signing key so cookie/login tokens are signed with
            // the same key IntegrationTestAuthHandler validates against.
            services.RemoveAll<IJwtTokenService>();
            services.AddSingleton<IJwtTokenService>(_ => new JwtTokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                Issuer = IntegrationTestAuthHandler.JwtIssuer,
                Audience = IntegrationTestAuthHandler.JwtAudience,
                SigningKey = IntegrationTestAuthHandler.JwtSigningKey,
                LifetimeMinutes = IntegrationTestAuthHandler.JwtLifetimeMinutes
            })));
            services.RemoveAll<IAppJwtValidator>();
            services.AddSingleton<IAppJwtValidator>(_ => new AppJwtValidator(Microsoft.Extensions.Options.Options.Create(new JwtOptions
            {
                Issuer = IntegrationTestAuthHandler.JwtIssuer,
                Audience = IntegrationTestAuthHandler.JwtAudience,
                SigningKey = IntegrationTestAuthHandler.JwtSigningKey,
                LifetimeMinutes = IntegrationTestAuthHandler.JwtLifetimeMinutes
            })));

            services.RemoveAll<ICurrentUserService>();
            services.AddScoped<ICurrentUserService, IntegrationTestCurrentUserService>();
        });
    }

    /// <summary>
    /// True when a ConversationTurnRecoveryService hosted service is still registered. Inspects the
    /// service collection's own factory closures by rebuilding the collection and asking the resulting
    /// IHostedService set which concrete types it would start.
    /// </summary>
    private static bool HasRecoveryHostedService(IServiceCollection services)
    {
        var recoveryType = typeof(GuideAntsApi.Services.Conversations.Recovery.ConversationTurnRecoveryService);
        using var probeProvider = services.BuildServiceProvider();
        foreach (var d in services)
        {
            if (d.ServiceType != typeof(IHostedService))
            {
                continue;
            }

            try
            {
                var concrete = d.ImplementationType
                    ?? d.ImplementationInstance?.GetType()
                    ?? d.ImplementationFactory?.Invoke(probeProvider).GetType();
                if (concrete == recoveryType)
                {
                    return true;
                }
            }
            catch
            {
                // Cannot resolve in probe; treat as not-recovery so unrelated factories do not fail startup.
            }
        }

        return false;
    }

    private static string GetTestProjectDirectory()
    {
        var currentDirectory = AppContext.BaseDirectory;
        while (currentDirectory != null)
        {
            var projectFile = Directory.GetFiles(currentDirectory, "GuideAntsApi.IntegrationTests.csproj").FirstOrDefault();
            if (projectFile != null)
            {
                return currentDirectory;
            }
            currentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        }
        throw new Exception("Could not find the test project directory.");
    }

    protected virtual void ConfigureTestAppConfiguration(IConfigurationBuilder config, WebHostBuilderContext context)
    {
        // Base implementation does nothing, derived classes can override.
    }

    public override async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(ConnectionStringEnvColon, _priorConnectionStringColon);
        Environment.SetEnvironmentVariable(ConnectionStringEnvDoubleUnderscore, _priorConnectionStringDoubleUnderscore);
        await base.DisposeAsync();
    }

    private sealed class NoOpLocalServiceLoadService : ILocalServiceLoadService
    {
        public IReadOnlyCollection<string> AllServiceIds { get; } =
            new[] { "SpeechTranscription", "Embeddings", "SpeechSynthesis", "ImageGeneration" };

        // These stubs never run a real load/unload, so nothing is ever in flight.
        public bool IsOperationInFlight(string serviceId) => false;

        public Task<LocalServiceReadiness> ProbeAsync(string serviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalServiceReadiness
            {
                ServiceId = serviceId,
                Configured = false,
                Loaded = false,
                Loading = false,
            });

        public Task<LocalServiceOperationResult> LoadServiceAsync(
            string serviceId,
            string modelRef,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalServiceOperationResult
            {
                ServiceId = serviceId,
                Success = true,
            });

        public Task<LocalServiceOperationResult> UnloadServiceAsync(
            string serviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalServiceOperationResult
            {
                ServiceId = serviceId,
                Success = true,
            });

        public Task<LocalServiceOperationResult> EnsureLoadedAsync(
            string serviceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalServiceOperationResult
            {
                ServiceId = serviceId,
                Success = true,
            });

        public Task<IReadOnlyList<LocalServiceOperationResult>> StartupEnsureAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalServiceOperationResult>>(
                AllServiceIds
                    .Select(id => new LocalServiceOperationResult { ServiceId = id, Success = true })
                    .ToList());
    }
}
