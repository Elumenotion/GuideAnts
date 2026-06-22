using GuideAntsApi.Configuration;
using GuideAntsApi.Database;
using GuideAntsApi.Services.Migrations;
using GuideAntsApi.Services.Mcp;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Endpoints.Settings;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Diagnostics;
using AntRunner.Chat;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using GuideAntsApi.Settings;

public class Program
{
    private const string NamedStorageMigrationSwitch = "--run-named-storage-migration";
    private const string NamedStorageMigrationApplySwitch = "--apply";
    private const string AsciiSlugNormalizationSwitch = "--run-ascii-slug-normalization";

    public static void Main(string[] args)
    {
        if (args.Contains(NamedStorageMigrationSwitch, StringComparer.OrdinalIgnoreCase))
        {
            RunNamedStorageMigration(args);
            return;
        }

        if (args.Contains(AsciiSlugNormalizationSwitch, StringComparer.OrdinalIgnoreCase))
        {
            RunAsciiSlugNormalization(args);
            return;
        }

        // Startup phase timing. Each LogPhase call reports the delta since the previous phase and the
        // cumulative time, so the slow step before the HTTP port opens is identifiable from container logs.
        var startupStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var startupTimingLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                o.SingleLine = true;
            });
        });
        var startupTimingLogger = startupTimingLoggerFactory.CreateLogger("StartupTiming");
        var lastPhaseElapsedMs = 0L;
        void LogPhaseStart(string phase)
        {
            startupTimingLogger.LogInformation(
                "Startup phase '{Phase}' starting at {TotalMs} ms",
                phase,
                startupStopwatch.ElapsedMilliseconds);
        }
        void LogPhase(string phase)
        {
            var nowMs = startupStopwatch.ElapsedMilliseconds;
            startupTimingLogger.LogInformation(
                "Startup phase '{Phase}' took {DeltaMs} ms (cumulative {TotalMs} ms)",
                phase, nowMs - lastPhaseElapsedMs, nowMs);
            lastPhaseElapsedMs = nowMs;
        }

        startupTimingLogger.LogInformation("Startup sequence started.");
        var runningInContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (runningInContainer &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE")))
        {
            // Linux bind-mounted workspaces can make file watcher initialization inside
            // WebApplication.CreateBuilder unexpectedly expensive. Keep hot-reload style
            // config file watching opt-in in containers to reduce cold-start latency.
            Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
            startupTimingLogger.LogInformation(
                "Startup optimization: set DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false for container startup.");
        }

        LogPhaseStart("WebApplication.CreateBuilder");
        var builder = WebApplication.CreateBuilder(args);
        LogPhase("WebApplication.CreateBuilder");
        startupTimingLogger.LogInformation(
            "Startup context: env={EnvironmentName}, inContainer={InContainer}, reloadConfigOnChange={ReloadConfigOnChange}, contentRoot={ContentRoot}",
            builder.Environment.EnvironmentName,
            runningInContainer,
            Environment.GetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE") ?? "(unset)",
            builder.Environment.ContentRootPath);

        if (builder.Configuration is IConfigurationRoot configurationRoot)
        {
            var providerNames = configurationRoot.Providers
                .Select(provider => provider.GetType().Name)
                .ToArray();
            startupTimingLogger.LogInformation(
                "Startup configuration providers ({ProviderCount}): {Providers}",
                providerNames.Length,
                string.Join(", ", providerNames));
        }

        // Normalize file storage to an absolute path early so every service and options binding
        // resolves the same notebook root regardless of the current working directory.
        var configuredFileStoragePath = builder.Configuration["FileStorage:Path"];
        if (!string.IsNullOrWhiteSpace(configuredFileStoragePath) && !Path.IsPathRooted(configuredFileStoragePath))
        {
            builder.Configuration["FileStorage:Path"] = Path.GetFullPath(
                configuredFileStoragePath,
                builder.Environment.ContentRootPath);
        }
        LogPhase("Normalize FileStorage path");

        // Snapshot pre-DB configuration for bootstrap seeding.
        var bootstrapConfiguration = new ConfigurationBuilder()
            .AddConfiguration(builder.Configuration)
            .Build();
        LogPhase("Build bootstrap configuration snapshot");

        var settingsSecrets = bootstrapConfiguration.GetSection(SettingsSecretsOptions.SectionName).Get<SettingsSecretsOptions>()
            ?? new SettingsSecretsOptions();
        var settingsSecretsErrors = ApplicationSettingsJson.ValidateSettingsSecrets(settingsSecrets);
        if (settingsSecretsErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SettingsSecrets configuration:\n - " + string.Join("\n - ", settingsSecretsErrors));
        }
        LogPhase("Validate SettingsSecrets");

        // Connection string for catalog creation + migrations. DB-backed settings are registered only after
        // EnsureCatalogAndMigrate so no configuration access triggers ApplicationSettingsConfigurationProvider.Load
        // before dbo.ApplicationSettings exists.
        var defaultConnectionString = bootstrapConfiguration.GetConnectionString("DefaultConnection");
        LogPhase("Read bootstrap connection string");

        // Configure FormOptions
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = long.MaxValue;
            options.ValueLengthLimit = int.MaxValue;
            options.MemoryBufferThreshold = int.MaxValue;
        });

        // Add services to the container.
        builder.Services.AddEndpointsApiExplorer();

        // Configure parameter binding
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        LogPhase("Configure baseline service options");

        // Configure all services using StartupConfiguration
        LogPhaseStart("ConfigureServices");
        StartupConfiguration.ConfigureServices(builder, LogPhase);
        startupTimingLogger.LogInformation(
            "Startup DI service descriptors registered: {DescriptorCount}",
            builder.Services.Count);
        LogPhase("ConfigureServices complete");

        if (!string.IsNullOrWhiteSpace(defaultConnectionString))
        {
            using var dbInitLoggerFactory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddSimpleConsole(o =>
                {
                    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    o.SingleLine = true;
                });
            });
            var dbInitLogger = dbInitLoggerFactory.CreateLogger("Database");
            SqlServerDatabaseInitializer.EnsureCatalogAndMigrate(defaultConnectionString, dbInitLogger);
            LogPhase("EnsureCatalogAndMigrate");
        }

        // Add DB-backed settings after schema exists so Build() and later config reads can load this provider safely.
        if (!string.IsNullOrWhiteSpace(defaultConnectionString))
        {
            ((IConfigurationBuilder)builder.Configuration).Add(new ApplicationSettingsConfigurationSource(
                defaultConnectionString,
                new SettingsSectionRegistry(),
                builder.Environment.ContentRootPath,
                settingsSecrets));
        }

        // Increase max request body size to allow larger file uploads (e.g., audio/video)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = long.MaxValue;
        });

        var app = builder.Build();
        LogPhase("builder.Build");

        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        ChatDiagnostics.Initialize(loggerFactory);
        ToolCallingDiagnostics.Initialize(loggerFactory);

        // Seed missing settings sections from bootstrap config and then force a config reload
        // so DB-primary values are active before service initialization.
        using (var scope = app.Services.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
            settingsService.BootstrapAsync(bootstrapConfiguration).GetAwaiter().GetResult();
            LogPhase("Settings bootstrap");
            settingsService.ReloadConfiguration();
            LogPhase("Settings reload");

            var requiredSeeder = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Bootstrap.IRequiredGuidesAssistantsSeeder>();
            requiredSeeder.SeedAsync().GetAwaiter().GetResult();
            LogPhase("RequiredGuidesAssistantsSeeder");

            var guideAntsSystemSeeder = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Bootstrap.IGuideAntsSystemSeeder>();
            guideAntsSystemSeeder.SeedAsync().GetAwaiter().GetResult();
            LogPhase("GuideAntsSystemSeeder");

            var runtimeProfileSeeder = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Bootstrap.IRuntimeProfileSeeder>();
            runtimeProfileSeeder.SeedAsync().GetAwaiter().GetResult();
            LogPhase("RuntimeProfileSeeder");

            var localServiceAutoSelector = scope.ServiceProvider.GetRequiredService<GuideAntsApi.Services.Bootstrap.ILocalServiceAutoSelector>();
            localServiceAutoSelector.AutoSelectAsync().GetAwaiter().GetResult();
            LogPhase("LocalServiceAutoSelector");

        }

        ServiceRoutingStartupValidator.Validate(app.Services.GetRequiredService<IConfiguration>());

        // Run local AI warmup asynchronously after host startup so slow model loads
        // (especially image generation) do not block API availability.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                using var warmupScope = app.Services.CreateScope();
                var localAiWarmup = warmupScope.ServiceProvider
                    .GetRequiredService<GuideAntsApi.Services.Bootstrap.ILocalAiStartupWarmupService>();
                try
                {
                    await localAiWarmup.WarmupAllAsync().ConfigureAwait(false);
                    LogPhase("LocalAiStartupWarmup (background)");
                }
                catch (Exception ex)
                {
                    var startupLogger = warmupScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                    startupLogger.LogWarning(ex, "Local AI startup warmup failed; continuing application startup.");
                }
            });
        });

        // Resolve auth values from live configuration first, then env fallback.
        var providerConfigResolver = app.Services.GetRequiredService<IProviderConfigurationResolver>();
        ToolCaller.ConfigurationVariableResolver = providerConfigResolver.ResolveConfigurationVariableName;

        // Set environment variables from configuration - CRITICAL for chat functionality
        // This ensures that Azure OpenAI and other API configurations are available as environment variables
        // which are required by the AntRunner.Chat library
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var environmentVariablesSet = 0;
        foreach (var setting in configuration.AsEnumerable())
        {
            if (!string.IsNullOrEmpty(setting.Value) && Environment.GetEnvironmentVariable(setting.Key) == null)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
                environmentVariablesSet++;
            }
        }
        startupTimingLogger.LogInformation(
            "Startup environment sync set {SetCount} variables from configuration.",
            environmentVariablesSet);
        LogPhase("Environment variable sync");

        // Initialize static service provider for NotebookDockerScriptService
        GuideAntsApi.Services.NotebookDockerScriptService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for NotebookPathHelper
        GuideAntsApi.Services.NotebookPathHelper.InitializeServiceProvider(app.Services);

        // Initialize static service provider for SandboxToolService
        GuideAntsApi.Services.SandboxToolService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for MemoryTools (KM queries)
        GuideAntsApi.Services.MemoryTools.InitializeServiceProvider(app.Services);

        // Initialize static service provider for ReadWeb tools
        GuideAntsApi.Services.ReadWebTools.InitializeServiceProvider(app.Services);

        // Initialize static service provider for NotebookImageService
        GuideAntsApi.Services.NotebookImageService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for NotebookPodcastService
        GuideAntsApi.Services.NotebookPodcastService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for UserProjectContextOptionsService
        GuideAntsApi.Services.UserProjectContextOptions.UserProjectContextOptionsStaticService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for Agent
        AntRunner.Chat.Agent.InitializeServiceProvider(app.Services);
        LogPhase("Static service provider initialization");

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "GuideAnts API v1");
            });
        }

        app.UseHttpsRedirection();
        app.UseCors("RestrictedOrigins");
        app.UseWebSockets();
        
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();

                // Routing failures (R-2) surface as RFC 7807 problem+json with a stable code.
                if (exception is GuideAntsApi.Services.Routing.RoutingException routingException)
                {
                    var problem = GuideAntsApi.Services.Routing.RoutingProblemDetailsFactory.Create(routingException);
                    logger.LogWarning(
                        routingException,
                        "Routing failure {Code} for {Method} {Path} serviceId={ServiceId} modelId={ModelId} modeId={ModeId}",
                        LogValueSanitizer.Sanitize(routingException.Code),
                        LogValueSanitizer.Sanitize(context.Request.Method),
                        LogValueSanitizer.Sanitize(context.Request.Path),
                        LogValueSanitizer.Sanitize(routingException.ServiceId),
                        LogValueSanitizer.Sanitize(routingException.ModelId),
                        LogValueSanitizer.Sanitize(routingException.ModeId));

                    context.Response.StatusCode = problem.Status ?? 500;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsJsonAsync(problem);
                    return;
                }

                if (exception != null)
                    logger.LogError(
                        exception,
                        "Unhandled exception for {Method} {Path}: {Message}",
                        LogValueSanitizer.Sanitize(context.Request.Method),
                        LogValueSanitizer.Sanitize(context.Request.Path),
                        LogValueSanitizer.Sanitize(exception.Message));

                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                var message = env.IsDevelopment() && exception != null
                    ? exception.Message
                    : "An unexpected error occurred";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Internal Server Error",
                    message
                });
            });
        });

        app.UseAuthentication();
        app.UseAuthorization();

        // MCP API key auth middleware — runs only on /api/published/mcp path
        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments("/api/published/mcp"),
            branch => branch.UseMiddleware<McpApiKeyMiddleware>()
        );

        app.MapGet("/api/startup", () => Results.Ok(new { status = "ready" }))
            // Startup readiness is a public host probe.
            .AllowAnonymous();
        
        app.MapProjectEndpoints();
        app.MapSystemGuideEndpoints();
        app.MapGuidesMarkdownEndpoints();
        app.MapCatalogEndpoints();
        app.MapGuidesEndpoints();
        app.MapGuidesPublishingEndpoints();
        app.MapAssistantsEndpoints();
        app.MapQuickStartEndpoints();
        app.MapProjectContentFileEndpoints();
        app.MapProjectContentFileMarkdownEndpoints();
        app.MapProjectFolderEndpoints();
        app.MapLinkEndpoints();
        app.MapAuthEndpoints();
        app.MapAdminUsersEndpoints();
        app.MapUserEndpoints();
        app.MapNotebookConversationsEndpoints();
        app.MapNotebookHeaderToolbarEndpoints();
        app.MapNotebookLlamaRuntimeEndpoints();
        app.MapUserConversationsEndpoints();
        app.MapPublishedNotebookConversationsEndpoints();
        app.MapPublishedGuidesEndpoints();
        app.MapPublishedOpenAiWireEndpoints();
        app.MapSpeechEndpoints();
        app.MapPublishedSpeechEndpoints();
        app.MapNotebookEndpoints();
        app.MapProjectExternalAuthEndpoints();
        app.MapHostFolderMountEndpoints();
        app.MapHostFolderMountInternalEndpoints();
        app.MapNotebookFileMarkdownEndpoints();
        app.MapFileLineageEndpoints();
        app.MapUsageEndpoints();
        app.MapGuideUsageEndpoints();
        app.MapSettingsEndpoints();
        app.MapDocumentServerEndpoints();

        app.MapMcp("/api/published/mcp")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        // Stateless Streamable HTTP does not map the GET (SSE) or DELETE endpoints. Without an
        // explicit handler the SPA catch-all fallback answers GET /api/published/mcp with a 404,
        // which clients such as Cursor treat as a fatal transport error and tombstone the server.
        // The Streamable HTTP spec requires a 405 here to signal "no server-initiated SSE stream";
        // compliant clients then operate POST-only. Registered before the SPA pipeline so it wins
        // over the fallback.
        app.MapMethods("/api/published/mcp", new[] { "GET", "DELETE" }, (HttpContext ctx) =>
            {
                ctx.Response.Headers.Allow = "POST";
                return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            })
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        app.UseGuideAntsUiPipeline(builder.Configuration);

        LogPhase("Pipeline + endpoint mapping");
        app.Lifetime.ApplicationStarted.Register(() =>
            startupTimingLogger.LogInformation(
                "Application started and listening on {Urls} after {TotalMs} ms total",
                string.Join(", ", app.Urls), startupStopwatch.ElapsedMilliseconds));

        app.Run();
    }

    private static void RunNamedStorageMigration(string[] args)
    {
        var options = new WebApplicationOptions
        {
            Args = args
        };
        var builder = WebApplication.CreateBuilder(options);

        var configuredFileStoragePath = builder.Configuration["FileStorage:Path"];
        if (string.IsNullOrWhiteSpace(configuredFileStoragePath))
        {
            throw new InvalidOperationException("FileStorage:Path is not configured.");
        }

        var storageRoot = Path.IsPathRooted(configuredFileStoragePath)
            ? configuredFileStoragePath
            : Path.GetFullPath(configuredFileStoragePath, builder.Environment.ContentRootPath);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        }

        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                o.SingleLine = true;
            });
        });

        var logger = loggerFactory.CreateLogger<NamedStorageMigrationRunner>();
        var runner = new NamedStorageMigrationRunner(connectionString, storageRoot, logger);
        var apply = args.Contains(NamedStorageMigrationApplySwitch, StringComparer.OrdinalIgnoreCase);
        runner.RunAsync(apply).GetAwaiter().GetResult();
    }

    private static void RunAsciiSlugNormalization(string[] args)
    {
        var options = new WebApplicationOptions
        {
            Args = args
        };
        var builder = WebApplication.CreateBuilder(options);

        var configuredFileStoragePath = builder.Configuration["FileStorage:Path"];
        if (string.IsNullOrWhiteSpace(configuredFileStoragePath))
        {
            throw new InvalidOperationException("FileStorage:Path is not configured.");
        }

        var storageRoot = Path.IsPathRooted(configuredFileStoragePath)
            ? configuredFileStoragePath
            : Path.GetFullPath(configuredFileStoragePath, builder.Environment.ContentRootPath);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        }

        using var loggerFactory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddSimpleConsole(o =>
            {
                o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                o.SingleLine = true;
            });
        });

        var logger = loggerFactory.CreateLogger<AsciiSlugNormalizationRunner>();
        var runner = new AsciiSlugNormalizationRunner(connectionString, storageRoot, logger);
        var apply = args.Contains(NamedStorageMigrationApplySwitch, StringComparer.OrdinalIgnoreCase);
        runner.RunAsync(apply).GetAwaiter().GetResult();
    }
}
