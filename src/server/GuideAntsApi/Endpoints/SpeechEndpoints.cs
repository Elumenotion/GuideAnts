using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Speech;
using GuideAntsApi.Services.Core;

namespace GuideAntsApi.Endpoints;

public static class SpeechEndpoints
{
    public static void MapSpeechEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/speech")
            .WithTags("Speech");

        // POST /api/speech/transcribe - Transcribe audio to text (JWT authenticated)
        group.MapPost("/transcribe", async (
            HttpContext ctx,
            [FromServices] ISpeechTranscriptionService transcriptionService,
            [FromQuery] string? language) =>
        {
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
        })
        .Produces<TranscriptionResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status500InternalServerError)
        .Produces(StatusCodes.Status504GatewayTimeout)
        .DisableAntiforgery(); // Required for multipart/form-data
    }
}

