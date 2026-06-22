using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Any;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Core;
using GuideAntsApi.BackgroundJobs;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Options;
using AntRunner.ToolCalling.Functions;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Conversations.Attachments;
using GuideAntsApi.Services.Conversations.Commands;
using GuideAntsApi.Services.Conversations.Mapping;
using GuideAntsApi.Services.Conversations.Queries;
using GuideAntsApi.Services.Conversations.Persistence;
using GuideAntsApi.Services.Conversations.Streaming;
using GuideAntsApi.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using AntRunner.Chat.Abstractions;
using AntRunner.Chat.Anthropic;
using AntRunner.Chat.GoogleGemini;
using AntRunner.Chat.HuggingFace;
using AntRunner.Chat.LlamaCpp;
using AntRunner.Chat.OpenAI;
using AntRunner.Chat.OpenRouter;
using Microsoft.AspNetCore.Authorization;
using GuideAntsApi.Settings;
using GuideAntsApi.Services.Infrastructure;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Services.NotebookHeaderToolbar;
using GuideAntsApi.Services.Mcp;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace GuideAntsApi.Configuration;

public static class StartupConfiguration
{
    public static void ConfigureServices(WebApplicationBuilder builder, Action<string>? phaseLogger = null)
    {
        ConfigureServices(builder.Services, builder.Configuration, phaseLogger);

        ConfigureSwagger(builder);
        phaseLogger?.Invoke("ConfigureServices.ConfigureSwagger");
    }

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration, Action<string>? phaseLogger = null)
    {
        ConfigureDatabase(services, configuration);
        phaseLogger?.Invoke("ConfigureServices.ConfigureDatabase");

        ConfigureAuthentication(services, configuration);
        phaseLogger?.Invoke("ConfigureServices.ConfigureAuthentication");

        ConfigureCors(services, configuration);
        phaseLogger?.Invoke("ConfigureServices.ConfigureCors");

        ConfigureMemoryCache(services);
        phaseLogger?.Invoke("ConfigureServices.ConfigureMemoryCache");

        ConfigureOptions(services, configuration);
        phaseLogger?.Invoke("ConfigureServices.ConfigureOptions");

        RegisterServices(services, configuration, phaseLogger);
        phaseLogger?.Invoke("ConfigureServices.RegisterServices");
    }

    private static void RegisterServices(IServiceCollection services, IConfiguration configuration, Action<string>? phaseLogger = null)
    {
        // Default HttpClient used by CreateClient() (e.g. published conversation stream) — 300s timeout for long-running LLM/tool calls
        services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(300));
        // HttpClient factory is already registered via AddHttpClient<ISpeechTranscriptionService>
        // Individual services now use IHttpClientFactory.CreateClient() for proper connection management
        services.AddReverseProxy();

        // EF Core logging interceptor for enhanced query debugging
        services.AddSingleton<EfQueryWarningInterceptor>();

        // Core Services
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, HttpUserContext>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IAdminUserService, AdminUserService>();
        services.AddScoped<IToolOAuthService, ToolOAuthService>();
        services.AddSingleton<IUserPasswordHasher, UserPasswordHasher>();
        services.AddSingleton<IStoragePathResolver, StoragePathResolver>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddSingleton<ISettingsSectionRegistry, SettingsSectionRegistry>();
        services.AddSingleton<IProviderConfigurationResolver, ProviderConfigurationResolver>();
        services.AddSingleton<IRuntimeProfileResolver, RuntimeProfileResolver>();
        services.AddSingleton<IServiceEditorMetadataProvider, ServiceEditorMetadataProvider>();
        services.AddScoped<IApplicationSettingsService, ApplicationSettingsService>();
        services.AddScoped<IEmbeddingsRebuildService, EmbeddingsRebuildService>();
        services.AddScoped<GuideAntsApi.Services.Bootstrap.IRequiredGuidesAssistantsSeeder, GuideAntsApi.Services.Bootstrap.RequiredGuidesAssistantsSeeder>();
        services.AddScoped<GuideAntsApi.Services.Bootstrap.IGuideAntsSystemSeeder, GuideAntsApi.Services.Bootstrap.GuideAntsSystemSeeder>();
        services.AddScoped<GuideAntsApi.Settings.IGuideAntsSystemSettingsStore, GuideAntsApi.Settings.GuideAntsSystemSettingsStore>();
        services.AddScoped<GuideAntsApi.Services.SystemGuide.ISystemProjectAccessGuard, GuideAntsApi.Services.SystemGuide.SystemProjectAccessGuard>();
        services.AddScoped<GuideAntsApi.Services.SystemGuide.ISystemGuideSessionService, GuideAntsApi.Services.SystemGuide.SystemGuideSessionService>();
        services.AddScoped<GuideAntsApi.Services.SystemGuide.ISystemGuideCatalogFilter, GuideAntsApi.Services.SystemGuide.SystemGuideCatalogFilter>();
        services.AddScoped<GuideAntsApi.Services.SystemGuide.ISystemGuideSandboxAdminProxy, GuideAntsApi.Services.SystemGuide.SystemGuideSandboxAdminProxy>();
        services.AddScoped<GuideAntsApi.Services.Bootstrap.IRuntimeProfileSeeder, GuideAntsApi.Services.Bootstrap.RuntimeProfileSeeder>();
        services.AddScoped<GuideAntsApi.Services.Bootstrap.ILocalServiceAutoSelector, GuideAntsApi.Services.Bootstrap.LocalServiceAutoSelector>();
        services.AddSingleton<GuideAntsApi.Services.Bootstrap.ILocalAiStartupWarmupService, GuideAntsApi.Services.Bootstrap.LocalAiStartupWarmupService>();
        services.AddHostedService<GuideAntsApi.Services.Bootstrap.LocalAiRuntimeWatchdogHostedService>();
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.CoreServices");

 
        // Guides Services
        services.AddScoped<GuideAntsApi.Services.Guides.ICatalogService, GuideAntsApi.Services.Guides.CatalogService>();
        services.AddScoped<GuideAntsApi.Services.Guides.IGuidesService, GuideAntsApi.Services.Guides.GuidesService>();
        services.AddScoped<GuideAntsApi.Services.Guides.IGuideExportImportService, GuideAntsApi.Services.Guides.GuideExportImportService>();
        services.AddScoped<GuideAntsApi.Services.Guides.IGuideUsageService, GuideAntsApi.Services.Guides.GuideUsageService>();

        // Component Services
        services.AddScoped<ILinkService, LinkService>();
        services.AddScoped<INotebookService, NotebookService>();
        services.AddScoped<IContentFileService, ContentFileService>();
        services.AddScoped<IProjectFolderService, ProjectFolderService>();
        services.AddScoped<IHostFolderMountService, HostFolderMountService>();
        services.AddScoped<ISemiStructuredDataService, SemiStructuredDataService>();
        services.AddScoped<IConversationQueryService, ConversationQueryService>();
        services.AddScoped<IConversationCommandService, ConversationCommandService>();
        services.AddScoped<IAttachmentContentService, AttachmentContentService>();
        services.AddScoped<IConversationHistoryBuilder, ConversationHistoryBuilder>();
        services.AddScoped<IConversationPersistence, ConversationPersistence>();
        services.AddScoped<IConversationUsageReporter, ConversationUsageReporter>();
        services.AddScoped<PrivateConversationStreamPolicy>();
        services.AddScoped<PublishedConversationStreamPolicy>();
        services.AddScoped<IConversationStreamEngine, ConversationStreamEngine>();
        services.AddScoped<IConversationUndoService, ConversationUndoService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IPublishedConversationService, PublishedConversationService>();
        services.AddScoped<GuideAntsApi.Services.Auth.IPublishedGuideAuthService, GuideAntsApi.Services.Auth.PublishedGuideAuthService>();
        services.AddScoped<GuideAntsApi.Services.PublishedWireApi.IPublishedApiExecutionContextResolver, GuideAntsApi.Services.PublishedWireApi.PublishedApiExecutionContextResolver>();
        services.AddScoped<GuideAntsApi.Services.PublishedWireApi.IPublishedWireUsageRecorder, GuideAntsApi.Services.PublishedWireApi.PublishedWireUsageRecorder>();
        services.AddScoped<IContextOptionsService, ContextOptionsService>();
        
        services.AddHttpClient<GuideAntsApi.Services.LlamaCpp.ILlamaServerRuntimeClient, GuideAntsApi.Services.LlamaCpp.LlamaServerRuntimeClient>(client =>
        {
            var config = configuration.GetSection("LlamaCpp");
            var baseUrl = config["BaseUrl"]
                ?? throw new InvalidOperationException("LlamaCpp:BaseUrl is required.");
            client.BaseAddress = new Uri(baseUrl);
        });
        services.AddHttpClient<ILlamaRuntimeAdminClient, LlamaRuntimeAdminClient>(client =>
        {
            var config = configuration.GetSection("LlamaCpp");
            var baseUrl = config["BaseUrl"]
                ?? throw new InvalidOperationException("LlamaCpp:BaseUrl is required.");
            client.BaseAddress = DeriveLlamaAdminBaseUri(baseUrl);
            client.Timeout = TimeSpan.FromHours(4);
        });
        services.AddScoped<ILlamaRouterIniSyncService, LlamaRouterIniSyncService>();
        services.AddScoped<GuideAntsApi.Services.LlamaCpp.INotebookModelRuntimeService, GuideAntsApi.Services.LlamaCpp.NotebookModelRuntimeService>();
        services.AddSingleton<IRouterModelsConfigService, RouterModelsConfigService>();
        services.AddScoped<ILlamaRuntimeInventoryService, LlamaRuntimeInventoryService>();
        services.AddScoped<ILocalModelOnboardingValidator, LocalModelOnboardingValidator>();
        services.AddScoped<ILocalModelOnboardingOrchestrator, LocalModelOnboardingOrchestrator>();
        services.AddSingleton<GuideAntsApi.Services.HuggingFace.IHuggingFaceTokenResolver, GuideAntsApi.Services.HuggingFace.HuggingFaceTokenResolver>();
        services.AddSingleton<IHuggingFaceModelDownloadService, HuggingFaceModelDownloadService>();
        // Thin HF REST adapter used by the Add Model wizard to populate file
        // pickers (Model file / mmproj file) from a pasted owner/repo. The
        // stored HF token is injected here so the browser never handles it.
        services.AddHttpClient<GuideAntsApi.Services.HuggingFace.IHuggingFaceRepositoryBrowser, GuideAntsApi.Services.HuggingFace.HuggingFaceRepositoryBrowser>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<GuideAntsApi.Services.UserProjectContextOptions.IUserProjectContextOptionsService, GuideAntsApi.Services.UserProjectContextOptions.UserProjectContextOptionsService>();
        services.AddScoped<GuideAnts.Usage.IUsageRecorder, GuideAnts.Usage.EfUsageRecorder>();
        services.AddScoped<INotebookCopyService, NotebookCopyService>();
        services.AddScoped<INotebookFileSyncService, NotebookFileSyncService>();
        services.AddSingleton<INotebookLockService, InMemoryNotebookLockService>();
        services.AddScoped<INotebookFileService, NotebookFileService>();
        services.AddScoped<IDocumentServerService, DocumentServerService>();
        services.AddScoped<IFileLineageService, FileLineageService>();
        services.AddScoped<IExcludedHostService, ExcludedHostService>();
        services.AddHttpClient<IBrowserRenderingClient, SearXngBrowserRenderingClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BrowserRenderingOptions>>().Value;
            client.Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, options.TimeoutMs));

            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                throw new InvalidOperationException("BrowserRendering:BaseUrl is required.");
            }

            var trimmedBaseUrl = options.BaseUrl.TrimEnd('/');
            if (Uri.TryCreate(trimmedBaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
                return;
            }

            throw new InvalidOperationException("BrowserRendering:BaseUrl must be an absolute URI.");
        });
        services.AddHttpClient<IWebSearchService, SearXngWebSearchService>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<SearXngSearchOptions>>().Value;
            client.Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, options.TimeoutMs));

            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                throw new InvalidOperationException("SearXngSearch:BaseUrl is required.");
            }

            var trimmedBaseUrl = options.BaseUrl.TrimEnd('/');
            if (Uri.TryCreate(trimmedBaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
                return;
            }

            throw new InvalidOperationException("SearXngSearch:BaseUrl must be an absolute URI.");
        });
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.LocalRuntimeAndSearch");


        // Usage Queries
        services.AddScoped<IUsageQueryService, UsageQueryService>();

        // Published guide cost limit evaluation (on-the-fly enforcement, no background jobs)
        services.AddScoped<GuideAntsApi.Services.PublishedGuides.IPublishedGuideCostLimitService, GuideAntsApi.Services.PublishedGuides.PublishedGuideCostLimitService>();

        // Conversation Management Services
        services.AddSingleton<IConversationBroadcastHub, ConversationBroadcastHub>();
        services.AddScoped<IDistributedConversationLock, DistributedConversationLockService>();
        services.AddHostedService<LockCleanupBackgroundService>();
        services.AddHostedService<HostFolderMountStartupReconciliationService>();
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.ConversationAndHosted");

        // LLM client abstraction (provider routing).
        //
        // The OpenAI SDK factories are wired twice, once per provider section:
        //   - Key "openai-platform" reads credentials from the OpenAI:* section
        //     (api.openai.com) and serves catalog rows whose Provider is
        //     "openai-chat" or "openai-responses".
        //   - Key "azure-openai" reads from AzureOpenAI:* and serves rows whose
        //     Provider is "azure-openai-chat" or "azure-openai-responses".
        // RoutingChatCompletionClientFactory resolves the right factory per
        // dispatch via [FromKeyedServices]; that's the whole point of splitting
        // them. Keep these two registrations symmetrical.
        services.AddKeyedSingleton(RoutingChatCompletionClientFactory.OpenAiPlatformFactoryKey, (provider, _) =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new OpenAiChatClientFactory(httpClientFactory, configAccessor: resolver.GetOpenAiConfig, loggerFactory: loggerFactory);
        });
        services.AddKeyedSingleton(RoutingChatCompletionClientFactory.AzureOpenAiFactoryKey, (provider, _) =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new OpenAiChatClientFactory(httpClientFactory, configAccessor: resolver.GetAzureOpenAiConfig, loggerFactory: loggerFactory);
        });
        services.AddKeyedSingleton(RoutingChatCompletionClientFactory.OpenAiPlatformFactoryKey, (provider, _) =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new OpenAiResponsesClientFactory(
                httpClientFactory,
                configAccessor: resolver.GetOpenAiConfig,
                loggerFactory: loggerFactory);
        });
        services.AddKeyedSingleton(RoutingChatCompletionClientFactory.AzureOpenAiFactoryKey, (provider, _) =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new OpenAiResponsesClientFactory(
                httpClientFactory,
                configAccessor: resolver.GetAzureOpenAiConfig,
                loggerFactory: loggerFactory);
        });
        services.AddSingleton(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new AnthropicChatClientFactory(httpClientFactory, configAccessor: resolver.GetAnthropicConfig, loggerFactory: loggerFactory);
        });
        services.AddSingleton(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var llamaConfig = new LlamaCppConfig();
            configuration.GetSection("LlamaCpp").Bind(llamaConfig);
            return new LlamaCppChatClientFactory(httpClientFactory, llamaConfig, loggerFactory);
        });
        services.AddSingleton(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new OpenRouterChatClientFactory(
                httpClientFactory,
                configAccessor: resolver.GetOpenRouterChatConfig,
                loggerFactory: loggerFactory);
        });
        services.AddSingleton(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new HuggingFaceChatClientFactory(
                httpClientFactory,
                configAccessor: resolver.GetHuggingFaceChatConfig,
                loggerFactory: loggerFactory);
        });
        services.AddSingleton(provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var resolver = provider.GetRequiredService<IProviderConfigurationResolver>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            return new GoogleGeminiChatClientFactory(
                httpClientFactory,
                configAccessor: resolver.GetGoogleGeminiChatConfig,
                loggerFactory: loggerFactory);
        });
        // Routing resolvers + validator + llama runtime coordinator (Phase A of settings-and-llama-completion plan).
        services.AddSingleton<IChatTargetResolver, ChatTargetResolver>();
        services.AddSingleton<IChatModelResolver, ChatModelResolver>();
        services.AddSingleton<IChatTargetValidator, ChatTargetValidator>();
        services.AddSingleton<IServiceModeResolver, ServiceModeResolver>();
        services.AddSingleton<ILlamaRuntimeCoordinator, LlamaRuntimeCoordinator>();
        services.AddScoped<IRoutingReadinessService, RoutingReadinessService>();
        services.AddExceptionHandler<RoutingExceptionHandler>();

        // Phase E (R-5.7): Infrastructure tab reachability probes. Dedicated
        // HttpClient so handler lifetime + 3s per-probe timeout (enforced by
        // linked CTS, not HttpClient.Timeout) are isolated from other clients.
        services.AddHttpClient(InfrastructureProbeService.HttpClientName);
        services.AddScoped<IInfrastructureProbeService, InfrastructureProbeService>();

        services.AddSingleton<IChatCompletionClientFactory, RoutingChatCompletionClientFactory>();
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.ChatFactoriesAndRouting");

        services.AddScoped<IConversationManager, ConversationManager>();
        services.AddScoped<INotebookHeaderToolbarService, NotebookHeaderToolbarService>();

        // Notebook Template Service
        services.AddScoped<INotebookTemplateService, NotebookTemplateService>();

        // Quick Start Service
        services.AddScoped<IQuickStartService, QuickStartService>();

        // Notebook Docker Script Service
        services.AddScoped<INotebookDockerScriptService, NotebookDockerScriptService>();
        // Sandbox Tool Service (Python sandbox execution)
        services.AddScoped<ISandboxToolService, SandboxToolService>();

        // Notebook Image Service
        services.AddScoped<INotebookImageService, NotebookImageService>();

        // Markdown extraction services (provider-routed extraction moved into BackgroundJobs library)
        services.AddHttpClient<ISpeechTranscriptionService, SpeechTranscriptionService>();
        services.AddHttpClient<IMediaExtractionClient, MediaExtractionClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptionsMonitor<VideoAudioExtractionOptions>>().CurrentValue;
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        });
        services.AddScoped<IVideoAudioExtractionService, VideoAudioExtractionService>();
        services.AddScoped<GuideAntsApi.BackgroundJobs.Services.ITranscriptionAdapter, SpeechTranscriptionAdapter>();
        services.AddScoped<IMarkdownExtractionService, MarkdownExtractionService>();
        services.AddScoped<MarkdownExtractionService>();  // Also register concrete type for GuidesService
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.NotebookAndMedia");
        
        // Background Jobs - replaces individual hosted services with queue-based processing
        services.AddBackgroundJobs(configuration);
        
        // Register job handlers
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.TestJobHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.ExtractContentVersionMarkdownHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.ExtractNotebookFileMarkdownHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.IndexContentMarkdownShadowHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.IndexNotebookMarkdownShadowHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.IndexDirectTextFileHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.SyncNotebookHandler>();
        // New transcription job handlers (audio/video routed via queue)
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.TranscribeContentVersionMarkdownHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.TranscribeNotebookFileMarkdownHandler>();
        // Assistant file markdown extraction and indexing
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.ExtractAssistantFileMarkdownHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.IndexAssistantFileMarkdownShadowHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.RebuildEmbeddingsHandler>();
        services.AddJobHandler<GuideAntsApi.BackgroundJobs.Jobs.RetentionCleanupHandler>();


        // Retention cleanup scheduler
        services.Configure<GuideAntsApi.BackgroundJobs.Options.RetentionCleanupOptions>(configuration.GetSection(GuideAntsApi.BackgroundJobs.Options.RetentionCleanupOptions.SectionName));
        services.AddHostedService<GuideAntsApi.BackgroundJobs.Services.RetentionCleanupScheduler>();
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.BackgroundJobs");
        
        // Keep existing services but they will now enqueue jobs instead of direct processing
        // services.AddHostedService<MarkdownExtractionBackgroundService>(); // Replaced by background jobs
        // ShadowIndexerService removed - Kernel Memory functionality replaced by HybridIndexer

        // Podcast generation – provider-routed speech synthesis (Azure/local TTS)
        services.AddHttpClient<ISpeechSynthesisService, SpeechSynthesisService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(15);
        });
        services.AddScoped<INotebookPodcastService, NotebookPodcastService>();

        // MCP (Model Context Protocol) server for published guides
        services.Configure<McpImageEmbeddingOptions>(
            configuration.GetSection(McpImageEmbeddingOptions.SectionName));
        services.AddScoped<McpPublishedGuideContext>();
        services.AddScoped<McpPublishedGuideInvokeService>();
        services.AddScoped<McpPublishedRunImageEmbedder>();
        services.AddScoped<IClaudeSkillPackService, ClaudeSkillPackService>();
        services.AddScoped<IMcpToolSourceDiscoveryService, McpToolSourceDiscoveryService>();
        services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithListToolsHandler(McpPublishedGuideToolHandlers.ListToolsAsync)
            .WithCallToolHandler(McpPublishedGuideToolHandlers.CallToolAsync);
        phaseLogger?.Invoke("ConfigureServices.RegisterServices.PodcastAndMcp");

    }

    private static void ConfigureDatabase(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                // Use single-query by default. SplitQuery was causing system-wide lock contention:
                // each split generates uncorrelated scans that block on concurrent write operations
                // (e.g., retention cleanup). Queries that genuinely need split behavior can opt in
                // with .AsSplitQuery() on a case-by-case basis.
                sqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                // Enable vector search functions
                sqlOptions.UseVectorSearch();
                // Resilient SQL execution and generous timeout for transient Azure SQL hiccups
                sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(60);
            });

            // Add interceptor for enhanced query logging with source information
            options.AddInterceptors(serviceProvider.GetRequiredService<EfQueryWarningInterceptor>());

            // Enhanced logging configuration for better debugging
            options.ConfigureWarnings(warnings =>
            {
                // In DEBUG, throw exceptions for multiple collection includes to identify exact source
#if DEBUG
                warnings.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning);
#endif

                // Log detailed information for query warnings with stack traces
                warnings.Log(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning,
                    Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.RowLimitingOperationWithoutOrderByWarning
                );
            });

            // Enable sensitive data logging in development for better debugging
#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        }, contextLifetime: ServiceLifetime.Scoped, optionsLifetime: ServiceLifetime.Singleton);

        // Also register a pooled factory for use by BackgroundJobs handlers (no scoped dependencies)
        services.AddPooledDbContextFactory<ApplicationDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery);
                sqlOptions.UseVectorSearch();
                // Resilient SQL execution and generous timeout for transient Azure SQL hiccups
                sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                sqlOptions.CommandTimeout(300);
            });

            options.ConfigureWarnings(warnings =>
            {
#if DEBUG
                warnings.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning);
#endif
                warnings.Log(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning,
                    Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.RowLimitingOperationWithoutOrderByWarning
                );
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });
    }

    private static void ConfigureAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        ValidateJwtOptions(jwtOptions);

        // Capture a single immutable snapshot so token issuance and bearer validation
        // cannot drift if runtime configuration providers reload Jwt settings later.
        var jwtOptionsSnapshot = new JwtOptions
        {
            Issuer = jwtOptions.Issuer,
            Audience = jwtOptions.Audience,
            SigningKey = jwtOptions.SigningKey,
            LifetimeMinutes = jwtOptions.LifetimeMinutes
        };
        services.AddSingleton<IJwtTokenService>(_ => new JwtTokenService(Microsoft.Extensions.Options.Options.Create(jwtOptionsSnapshot)));
        services.AddSingleton<IAppJwtValidator>(_ => new AppJwtValidator(Microsoft.Extensions.Options.Options.Create(jwtOptionsSnapshot)));
        services.AddSingleton<IAuthCookieService, AuthCookieService>();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = JwtTokenValidation.CreateParameters(jwtOptionsSnapshot);
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrEmpty(context.Token)
                            && context.Request.Cookies.TryGetValue(AuthCookieConstants.CookieName, out var cookieToken)
                            && !string.IsNullOrWhiteSpace(cookieToken))
                        {
                            context.Token = cookieToken;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var userIdValue = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        var securityStampValue = principal?.FindFirstValue(JwtClaimTypes.SecurityStamp);

                        if (!Guid.TryParse(userIdValue, out var userId) ||
                            !Guid.TryParse(securityStampValue, out var tokenSecurityStamp))
                        {
                            context.Fail("Token claims are invalid.");
                            return;
                        }

                        // Resolve identity (security stamp) AND live authority (role) in a single
                        // round-trip. The cookie/JWT proves only WHO the caller is; the role is
                        // mutable server state that an admin can change at any time, so RBAC must
                        // read it from the source of truth per request rather than trusting the
                        // role claim cached in the token. This is why an approved user no longer has
                        // to sign out and back in: the new role takes effect on the next request.
                        var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
                        var account = await db.UserRoles
                            .AsNoTracking()
                            .Where(userRole => userRole.UserId == userId)
                            .Select(userRole => new { userRole.User.SecurityStamp, userRole.Role })
                            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

                        if (account is null || account.SecurityStamp != tokenSecurityStamp)
                        {
                            context.Fail("Token security stamp mismatch.");
                            return;
                        }

                        // Replace the (potentially stale) role claim from the token with the live
                        // role so the authorization policies below evaluate current authority.
                        if (principal!.Identity is ClaimsIdentity identity)
                        {
                            AuthRoleClaims.ApplyLiveRole(identity, account.Role);
                        }

                        SlidingSessionRenewal.RenewIfNeeded(context, principal!, userId, tokenSecurityStamp);
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy("RequireApprovedUser", policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole(Role.Reader.ToString())
                    || context.User.IsInRole(Role.Contributor.ToString())
                    || context.User.IsInRole(Role.Admin.ToString())));

            options.AddPolicy("RequireContributor", policy =>
                policy.RequireAssertion(context =>
                    context.User.IsInRole(Role.Contributor.ToString())
                    || context.User.IsInRole(Role.Admin.ToString())));

            options.AddPolicy("RequireAdmin", policy =>
                policy.RequireRole(Role.Admin.ToString()));
        });
    }

    private static void ConfigureCors(IServiceCollection services, IConfiguration configuration)
    {
        // Read from IConfiguration (appsettings, env, etc.) instead of Environment variables,
        // because environment injection happens post-build in Program.cs
        var allowedOriginsEnv = configuration["ALLOWED_ORIGINS"] ?? configuration["AllowedOrigins"];
        if (string.IsNullOrWhiteSpace(allowedOriginsEnv))
        {
            throw new InvalidOperationException("ALLOWED_ORIGINS is not configured. Set ALLOWED_ORIGINS to a comma-separated list of allowed origins.");
        }
        var allowedProdOrigins = allowedOriginsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        services.AddCors(options =>
        {
            // Default restrictive policy for authenticated APIs - only allows specific origins
            options.AddPolicy("RestrictedOrigins", policy =>
            {
                policy
                    .SetIsOriginAllowed(origin =>
                    {
                        // Electron packaged apps may send Origin: null (file://)
                        if (string.IsNullOrEmpty(origin) || origin.Equals("null", StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                        {
                            // Allow any loopback (localhost/127.0.0.1/::1) on any port for Electron/dev
                            if (uri.IsLoopback) return true;
                        }

                        // Allow explicitly listed production origins from env var (comma-separated)
                        return allowedProdOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });

            // Open policy for public published APIs - allows all origins
            // Note: AllowAnyOrigin() and AllowCredentials() are mutually exclusive
            options.AddPolicy("PublicApiCors", policy =>
            {
                policy
                    .AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });
    }

    private static void ConfigureSwagger(WebApplicationBuilder builder)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "GuideAnts API",
                Version = "v1",
                Description = "API for the GuideAnts application",
                Contact = new OpenApiContact
                {
                    Name = "API Support",
                    Email = "support@example.com"
                }
            });

            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Description = "JWT Authorization header using the Bearer scheme.",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            });
            options.OperationFilter<BearerSecurityRequirementsOperationFilter>();

            // Configure ScriptType enum for Swagger
            options.MapType<ScriptType>(() => new OpenApiSchema
            {
                Type = "string",
                Enum = [.. Enum.GetNames(typeof(ScriptType)).Select(name => new OpenApiString(name) as IOpenApiAny)]
            });

            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            options.IncludeXmlComments(xmlPath);

        });
    }

    private static void ConfigureMemoryCache(IServiceCollection services)
    {
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = 1024; // Cache size limit in MB
        });
    }

    private static void ConfigureOptions(IServiceCollection services, IConfiguration configuration)
    {
        // Kernel Memory configuration removed - using HybridIndexer instead
        services.Configure<AzureDocumentIntelligenceOptions>(configuration.GetSection(AzureDocumentIntelligenceOptions.SectionName));
        services.Configure<DocumentIntelligenceOptions>(configuration.GetSection(DocumentIntelligenceOptions.SectionName));
        services.Configure<AzureSpeechServiceOptions>(configuration.GetSection(AzureSpeechServiceOptions.SectionName));
        services.Configure<SpeechTranscriptionOptions>(configuration.GetSection(SpeechTranscriptionOptions.SectionName));
        services.Configure<SpeechSynthesisOptions>(configuration.GetSection(SpeechSynthesisOptions.SectionName));
        services.Configure<ImageGenerationOptions>(configuration.GetSection(ImageGenerationOptions.SectionName));
        services.Configure<LocalServiceHostsOptions>(configuration.GetSection(LocalServiceHostsOptions.SectionName));
        services.Configure<VideoAudioExtractionOptions>(configuration.GetSection(VideoAudioExtractionOptions.SectionName));
        services.Configure<MarkdownExtractionOptions>(configuration.GetSection(MarkdownExtractionOptions.SectionName));
        services.Configure<MarkdownAttachmentOptions>(configuration.GetSection(MarkdownAttachmentOptions.SectionName));
        services.Configure<SearXngSearchOptions>(configuration.GetSection(SearXngSearchOptions.SectionName));
        services.Configure<BrowserRenderingOptions>(configuration.GetSection(BrowserRenderingOptions.SectionName));
        services.Configure<GoogleGeminiApiOptions>(configuration.GetSection(GoogleGeminiApiOptions.SectionName));
        services.Configure<OpenRouterOptions>(configuration.GetSection(OpenRouterOptions.SectionName));
        services.Configure<SettingsSecretsOptions>(configuration.GetSection(SettingsSecretsOptions.SectionName));
        services.Configure<LlamaModelManagementOptions>(configuration.GetSection(LlamaModelManagementOptions.SectionName));
        services.Configure<DocumentServerOptions>(configuration.GetSection(DocumentServerOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
    }

    private static void ValidateJwtOptions(JwtOptions jwtOptions)
    {
        if (string.IsNullOrWhiteSpace(jwtOptions.Issuer))
        {
            throw new InvalidOperationException("Jwt:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(jwtOptions.Audience))
        {
            throw new InvalidOperationException("Jwt:Audience is required.");
        }

        if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
        {
            throw new InvalidOperationException("Jwt:SigningKey is required.");
        }

        if (jwtOptions.SigningKey.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");
        }
    }

    private static Uri DeriveLlamaAdminBaseUri(string llamaBaseUrl)
    {
        var llamaUri = new Uri(llamaBaseUrl, UriKind.Absolute);
        var builder = new UriBuilder(llamaUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith(ServiceRoutingContracts.LlamaCppPath, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^ServiceRoutingContracts.LlamaCppPath.Length];
        }

        path = path.TrimEnd('/') + ServiceRoutingContracts.LlamaAdminPath + "/";
        builder.Path = path;
        return builder.Uri;
    }
}

internal sealed class BearerSecurityRequirementsOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var endpointMetadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (endpointMetadata == null || endpointMetadata.Count == 0)
        {
            return;
        }

        if (endpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        if (!endpointMetadata.OfType<IAuthorizeData>().Any())
        {
            return;
        }

        operation.Security ??= [];
        if (!operation.Security.Any(requirement => requirement.Keys.Any(key => key.Reference?.Id == "Bearer")))
        {
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                }] = Array.Empty<string>()
            });
        }

        operation.Responses.TryAdd(StatusCodes.Status401Unauthorized.ToString(), new OpenApiResponse
        {
            Description = "Unauthorized"
        });
        operation.Responses.TryAdd(StatusCodes.Status403Forbidden.ToString(), new OpenApiResponse
        {
            Description = "Forbidden"
        });
    }
}
