using GuideAntsApi.Endpoints.PublishedWire;
using GuideAntsApi.Services.SandboxWireApi;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints;

public static class SandboxOpenAiWireEndpoints
{
    public static void MapSandboxOpenAiWireEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/internal/sandbox/openai/v1")
            .WithTags("SandboxOpenAiWire")
            .AllowAnonymous();

        group.MapGet("/models", SandboxOpenAiWireHandlers.GetModelsAsync);
        group.MapPost("/chat/completions", SandboxOpenAiWireHandlers.PostChatCompletionsAsync);
        group.MapPost("/messages", SandboxOpenAiWireHandlers.PostMessagesAsync);
        group.MapPost("/responses", SandboxOpenAiWireHandlers.PostResponsesAsync);
        group.MapPost("/embeddings", SandboxOpenAiWireHandlers.PostEmbeddingsAsync);
        group.MapPost("/images/generations", SandboxOpenAiWireHandlers.PostImageGenerationsAsync);
        group.MapPost("/audio/transcriptions", SandboxOpenAiWireHandlers.PostAudioTranscriptionsAsync)
            .DisableAntiforgery();
        group.MapPost("/audio/speech", SandboxOpenAiWireHandlers.PostAudioSpeechAsync);
    }
}
