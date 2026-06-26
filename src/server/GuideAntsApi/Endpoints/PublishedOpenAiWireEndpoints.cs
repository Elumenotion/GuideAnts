using GuideAntsApi.Endpoints.PublishedWire;

namespace GuideAntsApi.Endpoints;

public static class PublishedOpenAiWireEndpoints
{
    public const string WireDiagnosticsLoggerCategory = "GuideAntsApi.Endpoints.PublishedOpenAiWireHandlers";

    public static void MapPublishedOpenAiWireEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/openai/{pubId:guid}/v1")
            .WithTags("PublishedOpenAiWire")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        group.MapGet("/models", PublishedWireModelsHandler.GetModelsAsync);
        group.MapPost("/chat/completions", PublishedOpenAiChatWireHandler.PostChatCompletionsAsync);
        group.MapPost("/responses", PublishedOpenAiChatWireHandler.PostResponsesAsync);
        group.MapPost("/embeddings", PublishedWireMediaHandler.PostEmbeddingsAsync);
        group.MapPost("/images/generations", PublishedWireMediaHandler.PostImageGenerationsAsync);
        group.MapPost("/audio/transcriptions", PublishedWireMediaHandler.PostAudioTranscriptionsAsync)
            .DisableAntiforgery();
        group.MapPost("/audio/speech", PublishedWireMediaHandler.PostAudioSpeechAsync);
    }
}

public static class PublishedAnthropicWireEndpoints
{
    public static void MapPublishedAnthropicWireEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/anthropic/{pubId:guid}/v1")
            .WithTags("PublishedAnthropicWire")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        group.MapPost("/messages", PublishedAnthropicWireHandler.PostMessagesAsync);
        group.MapPost("/messages/count_tokens", PublishedAnthropicWireHandler.PostMessagesCountTokensAsync);
    }
}
