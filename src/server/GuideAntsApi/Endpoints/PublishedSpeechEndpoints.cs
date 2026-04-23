using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Speech;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedGuides;

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
            [FromServices] ApplicationDbContext db,
            [FromServices] ISpeechTranscriptionService transcriptionService,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            [FromQuery] Guid? pubId,
            [FromQuery] string? language) =>
        {
            // Validate pubId
            if (!pubId.HasValue)
            {
                return Results.BadRequest(new { error = "Missing 'pubId' query parameter." });
            }

            // Look up the published guide to get project/notebook IDs
            var publishedGuide = await db.PublishedGuides
                .AsNoTracking()
                .Include(pg => pg.Notebook)
                .Where(pg => pg.Id == pubId.Value && pg.Active)
                .FirstOrDefaultAsync(ctx.RequestAborted);

            if (publishedGuide == null)
            {
                return Results.NotFound(new { error = "Published guide not found or inactive." });
            }

            var projectId = publishedGuide.Notebook.ProjectId;
            var notebookId = publishedGuide.NotebookId;

            // Validate authentication if required
            var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
            var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
            var authResult = await authService.ValidateAsync(
                pubId.Value, 
                authHeader, 
                projectId, 
                notebookId, 
                ctx.RequestAborted, 
                apiKeyHeader);

            if (!authResult.IsValid)
            {
                var statusCode = authResult.ErrorCode switch
                {
                    "authentication_required" or "api_key_required" => StatusCodes.Status401Unauthorized,
                    "invalid_token" or "invalid_api_key" => StatusCodes.Status401Unauthorized,
                    _ => StatusCodes.Status503ServiceUnavailable
                };

                return Results.Json(
                    new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                    statusCode: statusCode);
            }

            var limitResult = await costLimits.EnsureWithinLimitsAsync(notebookId, ctx.RequestAborted);
            if (!limitResult.Allowed)
            {
                return Results.Json(new
                {
                    error = "published_guide_cost_limit_exceeded",
                    reason = limitResult.Reason,
                    dailyLimitUsd = limitResult.DailyLimitUsd,
                    dailyChargeUsd = limitResult.DailyChargeUsd,
                    dailyWindowStartUtc = limitResult.DailyWindowStartUtc,
                    dailyWindowEndUtc = limitResult.DailyWindowEndUtc,
                    billingPeriodLimitUsd = limitResult.BillingPeriodLimitUsd,
                    billingPeriodChargeUsd = limitResult.BillingPeriodChargeUsd,
                    billingPeriodStartUtc = limitResult.BillingPeriodStartUtc,
                    billingPeriodEndUtc = limitResult.BillingPeriodEndUtc
                }, statusCode: StatusCodes.Status403Forbidden);
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
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status500InternalServerError)
        .Produces(StatusCodes.Status503ServiceUnavailable)
        .Produces(StatusCodes.Status504GatewayTimeout)
        .DisableAntiforgery(); // Required for multipart/form-data from external clients
    }
}

