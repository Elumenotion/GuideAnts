using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Speech;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedWireApi;
using GuideAnts.Usage;

namespace GuideAntsApi.Endpoints;

public static class PublishedSpeechEndpoints
{
    public static void MapPublishedSpeechEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/speech")
            .WithTags("PublishedSpeech")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        // POST /api/published/speech/transcribe?pubId={pubId} - Transcribe audio for published guides
        group.MapPost("/transcribe", async (
            HttpContext ctx,
            [FromServices] ISpeechTranscriptionService transcriptionService,
            [FromServices] IPublishedApiExecutionContextResolver executionContextResolver,
            [FromServices] IPublishedWireUsageRecorder wireUsageRecorder,
            [FromQuery] Guid? pubId,
            [FromQuery] string? language) =>
        {
            // Validate pubId
            if (!pubId.HasValue)
            {
                return Results.BadRequest(new { error = "Missing 'pubId' query parameter." });
            }

            var resolution = await executionContextResolver.ResolveAsync(
                ctx,
                pubId.Value,
                endpointName: "audio.transcriptions",
                ct: ctx.RequestAborted);
            if (!resolution.Success)
            {
                return resolution.ErrorResult!;
            }

            // Validate form data
            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
            {
                return Results.BadRequest(new { error = "No audio file provided. Use multipart/form-data with an 'audio' field." });
            }

            var audioFile = ctx.Request.Form.Files.GetFile("audio") ?? ctx.Request.Form.Files[0];
            if (audioFile == null || audioFile.Length == 0)
            {
                return Results.BadRequest(new { error = "Audio file is empty." });
            }

            var fileName = audioFile.FileName ?? "audio.webm";
            var contentType = audioFile.ContentType ?? "audio/webm";

            // Validate file type
            if (!transcriptionService.IsAudioFileSupported(fileName, contentType))
            {
                return Results.BadRequest(new { error = $"Unsupported audio format: {contentType}. Supported formats include: audio/webm, audio/wav, audio/mp3, audio/ogg, audio/opus, audio/aac, audio/flac, audio/mp4." });
            }

            // Validate file size
            if (!transcriptionService.IsFileSizeSupported(audioFile.Length))
            {
                return Results.BadRequest(new { error = "Audio file is too large. Maximum size is 300MB." });
            }

            try
            {
                using var stream = audioFile.OpenReadStream();
                // Disable diarization for mic input - single speaker, no need for speaker labels
                var result = await transcriptionService.TranscribeAudioWithDurationAsync(
                    stream, 
                    fileName, 
                    contentType, 
                    enableDiarization: false,
                    ctx.RequestAborted);
                var executionContext = resolution.Context!;
                var alias = executionContext.WireApiConfig.AliasMap?
                    .Where(kvp => string.Equals(kvp.Key, "transcription", StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Value)
                    .FirstOrDefault()
                    ?? "transcription";
                await wireUsageRecorder.RecordAsync(
                    context: executionContext,
                    category: UsageCategory.SpeechTranscription,
                    service: "SpeechTranscription",
                    operation: "audio.transcriptions",
                    metrics: new UsageMetrics(
                        ValueInput: result.DurationSeconds,
                        ValueOutput: result.Text.Length,
                        ValueOther: result.DurationSeconds),
                    endpoint: "audio.transcriptions",
                    status: "success",
                    alias: alias,
                    providerModel: null,
                    providerServiceMode: executionContext.WireApiConfig.Profile,
                    requestBytes: audioFile.Length,
                    inputCount: result.DurationSeconds,
                    outputCount: result.Text.Length,
                    ct: ctx.RequestAborted);

                return Results.Ok(new TranscriptionResponseDto
                {
                    Text = result.Text,
                    DurationSeconds = result.DurationSeconds
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (TimeoutException ex)
            {
                return Results.Json(
                    new { error = "transcription_timeout", message = ex.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    new { error = "transcription_failed", message = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = "usage_recording_failed", message = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .Produces<TranscriptionResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status500InternalServerError)
        .Produces(StatusCodes.Status503ServiceUnavailable)
        .Produces(StatusCodes.Status504GatewayTimeout)
        .DisableAntiforgery(); // Required for multipart/form-data from external clients
    }
}
