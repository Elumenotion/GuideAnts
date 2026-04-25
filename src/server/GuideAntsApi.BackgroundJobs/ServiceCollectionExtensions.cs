using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.BackgroundJobs.Services.Embeddings;
using GuideAntsApi.BackgroundJobs.Services.Indexing;
using GuideAntsApi.BackgroundJobs.Services.Search;

namespace GuideAntsApi.BackgroundJobs;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add background job processing services to the service collection
    /// </summary>
    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        // Configure options
        services.Configure<JobProcessorOptions>(configuration.GetSection(JobProcessorOptions.SectionName));
        services.Configure<AzureDocumentIntelligenceOptions>(configuration.GetSection(AzureDocumentIntelligenceOptions.SectionName));
        services.Configure<DocumentIntelligenceOptions>(configuration.GetSection(DocumentIntelligenceOptions.SectionName));
        services.Configure<MarkdownExtractionOptions>(configuration.GetSection(MarkdownExtractionOptions.SectionName));
        services.Configure<AzureOpenAiEmbeddingOptions>(configuration.GetSection(AzureOpenAiEmbeddingOptions.SectionName));
        services.Configure<EmbeddingsOptions>(configuration.GetSection(EmbeddingsOptions.SectionName));
        services.Configure<LocalServiceHostsOptions>(configuration.GetSection(LocalServiceHostsOptions.SectionName));
        
        // Register core services
        services.AddScoped<IJobQueueService, JobQueueService>();
        services.AddHostedService<BackgroundJobProcessor>();
        services.AddSingleton<DoclingConversionLimiter>();
        services.AddHttpClient<DoclingServeDocumentIntelligenceExtractor>(client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<IDocumentIntelligenceExtractor, AzureDocumentIntelligenceExtractor>();
        services.AddScoped<IDocumentIntelligenceExtractor>(sp => sp.GetRequiredService<DoclingServeDocumentIntelligenceExtractor>());
        services.AddScoped<IDocumentIntelligenceService, ProviderRoutedDocumentIntelligenceService>();
        
        // Register embedding and indexing services
        services.AddHttpClient<AzureOpenAiEmbeddingService>();
        services.AddHttpClient<LocalEmbeddingService>();
        services.AddHttpClient<GoogleGeminiEmbeddingService>();
        services.AddHttpClient<HuggingFaceEmbeddingService>();
        services.AddHttpClient<OpenRouterEmbeddingService>();
        services.AddSingleton<AzureOpenAiEmbeddingService>();
        services.AddSingleton<LocalEmbeddingService>();
        services.AddSingleton<GoogleGeminiEmbeddingService>();
        services.AddSingleton<HuggingFaceEmbeddingService>();
        services.AddSingleton<OpenRouterEmbeddingService>();
        services.AddSingleton<IEmbeddingService, ProviderRoutedEmbeddingService>();
        services.AddScoped<IHybridIndexer, HybridIndexer>();
        services.AddScoped<IHybridSearcher, HybridSearcher>();
        
        // Transcription handlers depend on ISpeechTranscriptionService and IVideoAudioExtractionService
        
        return services;
    }
    
    /// <summary>
    /// Register a job handler for a specific job type
    /// </summary>
    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services)
        where THandler : class, IJobHandler
    {
        services.AddScoped<IJobHandler, THandler>();
        return services;
    }
}
