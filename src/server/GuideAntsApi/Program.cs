using GuideAntsApi.Configuration;
using GuideAntsApi.Database;
using GuideAntsApi.Services.Migrations;
using GuideAntsApi.Endpoints;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Diagnostics;
using AntRunner.ToolCalling.Functions;
using GuideAntsApi.Settings;
using Microsoft.Extensions.Logging;

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

        var builder = WebApplication.CreateBuilder(args);

        // This API is DB-first for assistant definitions; do not fall back to file-based assistants.
        Environment.SetEnvironmentVariable("ASSISTANTS_DISABLE_FILE_FALLBACK", "true");

        // Normalize file storage to an absolute path early so every service and options binding
        // resolves the same notebook root regardless of the current working directory.
        var configuredFileStoragePath = builder.Configuration["FileStorage:Path"];
        if (!string.IsNullOrWhiteSpace(configuredFileStoragePath) && !Path.IsPathRooted(configuredFileStoragePath))
        {
            builder.Configuration["FileStorage:Path"] = Path.GetFullPath(
                configuredFileStoragePath,
                builder.Environment.ContentRootPath);
        }

        // Snapshot pre-DB configuration for bootstrap seeding.
        var bootstrapConfiguration = new ConfigurationBuilder()
            .AddConfiguration(builder.Configuration)
            .Build();
        var settingsSecrets = bootstrapConfiguration.GetSection(SettingsSecretsOptions.SectionName).Get<SettingsSecretsOptions>()
            ?? new SettingsSecretsOptions();
        var settingsSecretsErrors = ApplicationSettingsJson.ValidateSettingsSecrets(settingsSecrets);
        if (settingsSecretsErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SettingsSecrets configuration:\n - " + string.Join("\n - ", settingsSecretsErrors));
        }

        // Connection string for catalog creation + migrations. DB-backed settings are registered only after
        // EnsureCatalogAndMigrate so no configuration access triggers ApplicationSettingsConfigurationProvider.Load
        // before dbo.ApplicationSettings exists.
        var defaultConnectionString = bootstrapConfiguration.GetConnectionString("DefaultConnection");

        // Only enable console tracing in development to reduce log ingestion costs
        if (builder.Environment.IsDevelopment())
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
            System.Diagnostics.Trace.AutoFlush = true;
        }

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

        // Configure all services using StartupConfiguration
        StartupConfiguration.ConfigureServices(builder);

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

        // Seed missing settings sections from bootstrap config and then force a config reload
        // so DB-primary values are active before service initialization.
        using (var scope = app.Services.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<IApplicationSettingsService>();
            settingsService.BootstrapAsync(bootstrapConfiguration).GetAwaiter().GetResult();
            settingsService.ReloadConfiguration();
        }

        ServiceRoutingStartupValidator.Validate(app.Services.GetRequiredService<IConfiguration>());

        // Resolve auth values from live configuration first, then env fallback.
        var providerConfigResolver = app.Services.GetRequiredService<IProviderConfigurationResolver>();
        ToolCaller.ConfigurationVariableResolver = providerConfigResolver.ResolveConfigurationVariableName;

        // Set environment variables from configuration - CRITICAL for chat functionality
        // This ensures that Azure OpenAI and other API configurations are available as environment variables
        // which are required by the AntRunner.Chat library
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        foreach (var setting in configuration.AsEnumerable())
        {
            if (!string.IsNullOrEmpty(setting.Value) && Environment.GetEnvironmentVariable(setting.Key) == null)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
        }

        // Initialize static service provider for NotebookDockerScriptService
        GuideAntsApi.Services.NotebookDockerScriptService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for NotebookPathHelper
        GuideAntsApi.Services.NotebookPathHelper.InitializeServiceProvider(app.Services);

        // Initialize static service provider for SandboxToolService
        GuideAntsApi.Services.SandboxToolService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for MemoryTools (KM queries)
        GuideAntsApi.Services.MemoryTools.InitializeServiceProvider(app.Services);

        // Initialize static service provider for NotebookImageService
        GuideAntsApi.Services.NotebookImageService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for NotebookPodcastService
        GuideAntsApi.Services.NotebookPodcastService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for UserProjectContextOptionsService
        GuideAntsApi.Services.UserProjectContextOptions.UserProjectContextOptionsStaticService.InitializeServiceProvider(app.Services);

        // Initialize static service provider for Agent
        AntRunner.Chat.Agent.InitializeServiceProvider(app.Services);

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
                        routingException.Code,
                        context.Request.Method,
                        context.Request.Path,
                        routingException.ServiceId,
                        routingException.ModelId,
                        routingException.ModeId);

                    context.Response.StatusCode = problem.Status ?? 500;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsJsonAsync(problem);
                    return;
                }

                if (exception != null)
                    logger.LogError(exception, "Unhandled exception for {Method} {Path}: {Message}", context.Request.Method, context.Request.Path, exception.Message);

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

        app.MapGet("/api/startup", () => Results.Ok(new { status = "ready" }));
        
        app.MapProjectEndpoints();
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
        app.MapUserEndpoints();
        app.MapWebScrapingEndpoints();
        app.MapChatEndpoints();
        app.MapSandboxEndpoints();
        app.MapNotebookConversationsEndpoints();
        app.MapNotebookHeaderToolbarEndpoints();
        app.MapNotebookLlamaRuntimeEndpoints();
        app.MapUserConversationsEndpoints();
        app.MapPublishedNotebookConversationsEndpoints();
        app.MapPublishedGuidesEndpoints();
        app.MapSpeechEndpoints();
        app.MapPublishedSpeechEndpoints();
        app.MapNotebookEndpoints();
        app.MapProjectExternalAuthEndpoints();
        app.MapNotebookFileMarkdownEndpoints();
        app.MapFileLineageEndpoints();
        app.MapUsageEndpoints();
        app.MapGuideUsageEndpoints();
        app.MapSettingsEndpoints();
        app.UseGuideAntsUiPipeline(builder.Configuration);

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
