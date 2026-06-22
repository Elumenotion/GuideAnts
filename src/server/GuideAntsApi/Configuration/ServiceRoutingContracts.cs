namespace GuideAntsApi.Configuration;

internal static class ServiceRoutingContracts
{
    public const string GuideantsAiContainerName = "guideants-ai";
    public const string PlantUmlContainerName = "plantuml";

    public const string LlamaBaseUrlKey = "LlamaCpp:BaseUrl";
    public const string GuideantsAiBaseUrlKey = "ServiceRouting:Containers:guideants-ai:BaseUrl";

    public const string SandboxPath = "/sandbox";
    public const string LlamaCppPath = "/llama-cpp";
    public const string LlamaAdminPath = "/llama-admin";
    public const string LlamaHealthPath = "/llama-cpp/health";
    public const string DocumentIntelligenceBaseUrlKey = "LocalServiceHosts:DocumentIntelligenceBaseUrl";
    public const string DoclingVersionPath = "/version";

    public const string ImageGenerationAdminPath = "/sd";
    public const string SpeechTranscriptionAdminPath = "/asr";
    public const string SpeechSynthesisAdminPath = "/tts";
    public const string EmbeddingsAdminPath = "/emb";

    public static string ContainerBaseUrlKey(string containerName) =>
        $"ServiceRouting:Containers:{containerName}:BaseUrl";
}

