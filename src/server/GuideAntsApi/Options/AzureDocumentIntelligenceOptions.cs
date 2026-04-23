namespace GuideAntsApi.Options
{
    public static class ServiceProviderIds
    {
        public const string SpeechTranscriptionAzureSpeechBatch = "SpeechTranscription.AzureSpeech.Batch";
        public const string SpeechTranscriptionLocalAsrHttp = "SpeechTranscription.LocalAsr.Http";

        public const string SpeechSynthesisAzureSpeechSsml = "SpeechSynthesis.AzureSpeech.Ssml";
        public const string SpeechSynthesisLocalTtsHttp = "SpeechSynthesis.LocalTts.Http";

        public const string ImageGenerationAzureOpenAiImages = "ImageGeneration.AzureOpenAI.Images";
        public const string ImageGenerationLocalSdHttp = "ImageGeneration.LocalSd.Http";

        public const string EmbeddingsAzureOpenAiEmbedding = "Embeddings.AzureOpenAI.Embedding";
        public const string EmbeddingsLocalEmbHttp = "Embeddings.LocalEmb.Http";

        public const string DocumentIntelligenceAzure = "DocumentIntelligence.Azure.DocumentIntelligence";
        public const string DocumentIntelligenceLocalDoclingHttp = "DocumentIntelligence.LocalDocling.Http";
    }

    public class LocalServiceHostsOptions
    {
        public const string SectionName = "LocalServiceHosts";

        public string SpeechTranscriptionBaseUrl { get; set; } = "http://localhost:8110";
        public string SpeechSynthesisBaseUrl { get; set; } = "http://localhost:8110";
        public string ImageGenerationBaseUrl { get; set; } = "http://localhost:8110";
        public string EmbeddingsBaseUrl { get; set; } = "http://localhost:8110";
        public string MediaBaseUrl { get; set; } = "http://localhost:8110";
        public string DocumentIntelligenceBaseUrl { get; set; } = "http://localhost:5001";
    }

    public class AzureDocumentIntelligenceOptions
    {
        public const string SectionName = "AzureDocumentIntelligence";

        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiVersion { get; set; } = "2024-11-30";
        public int TimeoutSeconds { get; set; } = 300;
        public int MaxRetries { get; set; } = 3;
    }

    public class DocumentIntelligenceOptions
    {
        public const string SectionName = "DocumentIntelligence";

        public int TimeoutSeconds { get; set; } = 300;
        public int MaxConcurrentConversions { get; set; } = 1;
        public int AsyncStatusPollIntervalMs { get; set; } = 2000;
    }

    public class AzureSpeechServiceOptions
    {
        public const string SectionName = "AzureSpeechService";

        public string Endpoint { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Region { get; set; } = "eastus";
        public int TimeoutSeconds { get; set; } = 600;
        public int MaxRetries { get; set; } = 3;
    }

    public class SpeechTranscriptionOptions
    {
        public const string SectionName = "SpeechTranscription";

        public int TimeoutSeconds { get; set; } = 300;
    }

    public class SpeechSynthesisOptions
    {
        public const string SectionName = "SpeechSynthesis";

        public int TimeoutSeconds { get; set; } = 300;
    }

    public class ImageGenerationOptions
    {
        public const string SectionName = "ImageGeneration";

        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>Default output format for the local Stable Diffusion HTTP path (png, jpeg, or webp).</summary>
        public string LocalOutputFormat { get; set; } = "png";
    }

    public class MarkdownExtractionOptions
    {
        public const string SectionName = "MarkdownExtraction";

        public bool Enabled { get; set; } = true;
        public int ProcessingIntervalSeconds { get; set; } = 30;
        public int BatchSize { get; set; } = 5;
        public int MaxFileSizeMB { get; set; } = 500;
        public string[] SupportedExtensions { get; set; } = Array.Empty<string>();
    }

    public class VideoAudioExtractionOptions
    {
        public const string SectionName = "VideoAudioExtraction";

        public string AudioFormat { get; set; } = "mp3";
        public string AudioQuality { get; set; } = "2";
        public int TimeoutSeconds { get; set; } = 1800; // 30 minutes for large videos
        public long MaxFileSizeMB { get; set; } = 2048; // 2GB max video size
    }
}
