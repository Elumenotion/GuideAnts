using GuideAntsApi.Configuration;

namespace GuideAntsApi.Endpoints.Settings;

internal static class SettingsGroupFactory
{
    public static RouteGroupBuilder MapCoreGroup(WebApplication app) =>
        app.MapGroup("/api/settings")
            .WithTags("Settings")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

    public static RouteGroupBuilder MapServiceEditorsGroup(WebApplication app) =>
        app.MapGroup("/api/settings/services")
            .WithTags("SettingsServiceEditors")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

    public static RouteGroupBuilder MapRoutingGroup(WebApplication app) =>
        app.MapGroup("/api/settings/routing")
            .WithTags("SettingsRouting")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

    public static RouteGroupBuilder MapLlamaGroup(WebApplication app) =>
        app.MapGroup("/api/settings/llama")
            .WithTags("SettingsLlama")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

    public static RouteGroupBuilder MapHuggingFaceGroup(WebApplication app) =>
        app.MapGroup("/api/settings/huggingface")
            .WithTags("SettingsHuggingFace")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

    public static bool HasConfiguredLlamaRuntime(IConfiguration configuration) =>
        RuntimeConfigurationPlaceholders.HasUsableUrl(configuration["LlamaCpp:BaseUrl"]);

    public static IResult LlamaRuntimeUnavailable() =>
        Results.Json(
            new
            {
                error = "No local llama server is configured for this container yet.",
                code = "LLAMA_RUNTIME_UNAVAILABLE",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public static IResult LocalServiceUnavailable(string serviceId)
    {
        var displayName = serviceId switch
        {
            "SpeechTranscription" => "local ASR server",
            "SpeechSynthesis" => "local TTS server",
            "Embeddings" => "local embeddings server",
            "ImageGeneration" => "local image-generation server",
            _ => "local service server",
        };

        return Results.Json(
            new
            {
                error = $"No {displayName} is configured for this container yet.",
                code = "LOCAL_SERVICE_UNAVAILABLE",
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
