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

            // Integration tests should not depend on external LLM providers.
            services.RemoveAll<IChatCompletionClientFactory>();
            services.AddSingleton<IChatCompletionClientFactory, FakeChatCompletionClientFactory>();

            // Integration tests should not block on local AI auxiliary service
            // warmup/load-unload polling when calling settings endpoints.
            services.RemoveAll<ILocalAiStartupWarmupService>();
            services.AddSingleton<ILocalAiStartupWarmupService, NoOpLocalAiStartupWarmupService>();

            // appsettings.test.json points LlamaCpp / LocalServiceHosts at localhost:8110.
            // The real admin + warmup orchestration HttpClients use a 4-hour timeout; when
            // something accepts TCP but never answers, overview / llama auth-gate tests hang
            // until the test HttpClient aborts (~100s). Stub both to keep the suite hermetic.
            services.RemoveAll<ILlamaRuntimeAdminClient>();
            services.AddSingleton<ILlamaRuntimeAdminClient, StubLlamaRuntimeAdminClient>();
            services.RemoveAll<ILocalAiWarmupOrchestrationClient>();
            services.AddSingleton<ILocalAiWarmupOrchestrationClient, StubLocalAiWarmupOrchestrationClient>();

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

    private sealed class NoOpLocalAiStartupWarmupService : ILocalAiStartupWarmupService, ILocalAiWarmupService
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

        public Task SyncDesiredAndApplyAsync(
            WarmupDesiredBuildOptions? options = null,
            bool waitForCompletion = false,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

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
