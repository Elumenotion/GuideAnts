using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Auth;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Services.PublishedGuides;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Usage;

namespace GuideAntsApi.Endpoints;

public static class PublishedNotebookConversationsEndpoints
{
    public static void MapPublishedNotebookConversationsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/published/projects/{projectId:guid}/notebooks/{notebookId:guid}/conversations")
            .WithTags("PublishedNotebookConversations")
            .AllowAnonymous()
            .RequireCors("PublicApiCors");

        // Create (no auth, no credits requirement) - no trailing slash so clients can POST .../conversations
        group.MapPost(string.Empty, async (
            HttpContext ctx,
            [FromServices] IPublishedConversationService svc,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid projectId,
            Guid notebookId,
            [FromBody] NotebookConversationsEndpoints.CreateConversationRequest req) =>
        {
            var pubId = ctx.Request.Query["pubId"].ToString();
            if (!string.IsNullOrWhiteSpace(pubId) && Guid.TryParse(pubId, out var pubGuid))
            {
                // Use a custom header for published-guide authentication.
                var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
                var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
                var authResult = await authService.ValidateAsync(pubGuid, authHeader, projectId, notebookId, ctx.RequestAborted, apiKeyHeader, PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));

                if (!authResult.IsValid)
                {
                    var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

                    return Results.Json(
                        new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                        statusCode: statusCode);
                }

                SetPublishedChatUsageAttribution(ctx, pubGuid, authResult.UserIdentity);
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

            var dto = await svc.CreateConversationAsync(notebookId, req.Title);
            return Results.Ok(dto);
        })
        .Produces<NotebookConversationListDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        // Get single conversation with messages (no auth)
        group.MapGet("/{convoId:guid}", async (
            HttpContext ctx,
            [FromServices] IPublishedConversationService svc,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid projectId,
            Guid notebookId,
            Guid convoId) =>
        {
            var pubId = ctx.Request.Query["pubId"].ToString();
            if (!string.IsNullOrWhiteSpace(pubId) && Guid.TryParse(pubId, out var pubGuid))
            {
                var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
                var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
                var authResult = await authService.ValidateAsync(pubGuid, authHeader, projectId, notebookId, ctx.RequestAborted, apiKeyHeader, PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));

                if (!authResult.IsValid)
                {
                    var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

                    return Results.Json(
                        new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                        statusCode: statusCode);
                }

                SetPublishedChatUsageAttribution(ctx, pubGuid, authResult.UserIdentity);
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

            var conversation = await svc.GetConversationWithMessagesAsync(convoId);
            return conversation == null ? Results.NotFound() : Results.Ok(conversation);
        })
        .Produces<NotebookConversationWithMessagesDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status503ServiceUnavailable);

		// POST send message (SSE) - published (no principal)
		group.MapPost("/{convoId:guid}/messages", async (
            HttpContext ctx,
            [FromServices] IPublishedConversationService publishedService,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid projectId,
            Guid notebookId,
            Guid convoId,
            [FromBody] SendMessageRequest request) =>
        {
            var accept = ctx.Request.Headers["Accept"].ToString();
            if (!accept.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "This endpoint requires 'Accept: text/event-stream' header for streaming responses." });
            }

			var pubId = ctx.Request.Query["pubId"].ToString();
			if (string.IsNullOrWhiteSpace(pubId) || !Guid.TryParse(pubId, out var pubGuid))
			{
				return Results.BadRequest(new { error = "Missing or invalid 'pubId' query parameter." });
			}

			// Validate authentication from the custom published-guide auth header.
			var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
			var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
			var authResult = await authService.ValidateAsync(pubGuid, authHeader, projectId, notebookId, ctx.RequestAborted, apiKeyHeader, PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));

			if (!authResult.IsValid)
			{
				var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

				return Results.Json(
					new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
					statusCode: statusCode);
			}

            SetPublishedChatUsageAttribution(ctx, pubGuid, authResult.UserIdentity);

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

			ctx.Response.Headers["Content-Type"] = "text/event-stream";

			await foreach (var ev in publishedService.SendMessageStreamAsync(convoId, request, pubId, authResult.UserIdentity, authResult.InternalUserId, ctx.RequestAborted))
            {
                await ctx.Response.WriteSseEventAsync(ev.EventType, ev.Payload, ctx.RequestAborted);
            }

            return Results.Empty;
        })
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status503ServiceUnavailable);

		// GET published file content (anonymous, guarded by convoId + pubId)
		group.MapGet("/{convoId:guid}/files/content", async (
			[FromServices] GuideAntsApi.DataModel.ApplicationDbContext db,
			[FromServices] GuideAntsApi.Services.Components.INotebookFileService fileService,
			Guid projectId,
			Guid notebookId,
			Guid convoId,
			[FromQuery] string path,
			[FromQuery] string pubId) =>
		{
			// Basic guards
			if (string.IsNullOrWhiteSpace(path))
			{
				return Results.BadRequest(new { error = "Missing 'path' query parameter." });
			}
			if (string.IsNullOrWhiteSpace(pubId))
			{
				return Results.BadRequest(new { error = "Missing 'pubId' query parameter." });
			}

			// Validate conversation belongs to project/notebook
			var convo = await db.NotebookConversations
				.AsNoTracking()
				.Include(c => c.Notebook)
				.FirstOrDefaultAsync(c => c.Id == convoId);
			if (convo == null || convo.Notebook == null || convo.Notebook.Id != notebookId || convo.Notebook.ProjectId != projectId)
			{
				return Results.NotFound();
			}

			// Normalize relative path
			var normalizedPath = path.Replace("\\", "/");

			try
			{
				var res = await fileService.GetFileContentStreamAsync(projectId, notebookId, normalizedPath);
				return Results.Stream(res.stream, res.contentType);
			}
			catch (FileNotFoundException)
			{
				return Results.NotFound();
			}
		})
		.Produces(StatusCodes.Status200OK)
		.Produces(StatusCodes.Status400BadRequest)
		.Produces(StatusCodes.Status404NotFound);

        // POST upload files for published guide conversations
        group.MapPost("/files", async (
            HttpContext ctx,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            [FromServices] GuideAntsApi.DataModel.ApplicationDbContext db,
            [FromServices] IStoragePathResolver pathResolver,
            [FromServices] IMarkdownExtractionService markdownService,
            [FromServices] IServiceScopeFactory scopeFactory,
            Guid projectId,
            Guid notebookId) =>
        {
            var pubId = ctx.Request.Query["pubId"].ToString();
            if (string.IsNullOrWhiteSpace(pubId) || !Guid.TryParse(pubId, out var pubGuid))
            {
                return Results.BadRequest(new { error = "Missing or invalid 'pubId' query parameter." });
            }

            var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
            var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
            var authResult = await authService.ValidateAsync(pubGuid, authHeader, projectId, notebookId, ctx.RequestAborted, apiKeyHeader, PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));
            if (!authResult.IsValid)
            {
                var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

                return Results.Json(
                    new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                    statusCode: statusCode);
            }

            SetPublishedChatUsageAttribution(ctx, pubGuid, authResult.UserIdentity);

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

            if (!ctx.Request.HasFormContentType || ctx.Request.Form.Files.Count == 0)
            {
                return Results.BadRequest(new { error = "No files provided. Use multipart/form-data with file uploads." });
            }

            try
            {
                var notebookRoot = pathResolver.GetNotebookRootPath(projectId, notebookId);
                if (!NotebookStoragePath.TryResolveDirectoryUnderRoot(notebookRoot, "uploads", out var targetDirectory))
                {
                    return Results.BadRequest(new { error = "Invalid upload target path." });
                }

                Directory.CreateDirectory(targetDirectory);

                var uploadedDtos = new List<object>();

                foreach (var file in ctx.Request.Form.Files)
                {
                    if (file.Length == 0) continue;

                    var safeFileName = NotebookStoragePath.SanitizeFileName(file.FileName);
                    if (safeFileName == null)
                    {
                        return Results.BadRequest(new { error = "Invalid file name." });
                    }

                    if (!NotebookStoragePath.TryResolveUnderRoot(targetDirectory, safeFileName, out var physicalPath))
                    {
                        return Results.BadRequest(new { error = "Invalid upload file path." });
                    }

                    var relativePath = Path.GetRelativePath(notebookRoot, physicalPath).Replace("\\", "/");

                    await using (var stream = new FileStream(physicalPath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    // Compute Hash
                    string fileHash;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fileHash = Convert.ToHexString(sha.ComputeHash(stream));
                    }

                    var fileInfo = new FileInfo(physicalPath);

                    var existingFile = await db.NotebookFiles.FirstOrDefaultAsync(f => f.NotebookId == notebookId && f.RelativePath == relativePath);
                    NotebookFile notebookFile;

                    if (existingFile != null)
                    {
                        existingFile.FileSize = fileInfo.Length;
                        existingFile.LastModifiedUtc = fileInfo.LastWriteTimeUtc;
                        existingFile.FileHash = fileHash;
                        notebookFile = existingFile;
                    }
                    else
                    {
                        notebookFile = new NotebookFile
                        {
                            NotebookId = notebookId,
                            RelativePath = relativePath,
                            FileSize = fileInfo.Length,
                            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
                            FileHash = fileHash,
                            OriginContentFileVersionId = null
                        };
                        notebookFile.GenerateDocumentId(notebookId);
                        db.NotebookFiles.Add(notebookFile);
                    }
                    
                    await db.SaveChangesAsync();
                    
                    uploadedDtos.Add(new
                    {
                        notebookFileId = notebookFile.Id,
                        uploadType = DetectUploadType(safeFileName)
                    });

                    // Fire-and-forget processing
                    var fileId = notebookFile.Id;
                    _ = Task.Run(async () =>
                    {
                        try 
                        {
                            await markdownService.CreateNotebookMarkdownShadowAsync(fileId);
                            
                            using var scope = scopeFactory.CreateScope();
                            var jobQueue = scope.ServiceProvider.GetRequiredService<GuideAntsApi.BackgroundJobs.IJobQueueService>();
                            await jobQueue.EnqueueAsync(
                                jobType: "SyncNotebook",
                                payload: new GuideAntsApi.BackgroundJobs.Jobs.SyncNotebookJob(notebookId));
                        }
                        catch { /* best effort */ }
                    });
                }

                return Results.Ok(uploadedDtos);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new { error = "upload_failed", message = ex.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .Produces<List<object>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status500InternalServerError)
        .Produces(StatusCodes.Status503ServiceUnavailable)
        .DisableAntiforgery(); // Required for multipart/form-data from external clients

        // DELETE undo last (no auth)
        group.MapDelete("/{convoId:guid}/messages/last", async (
            HttpContext ctx,
            [FromServices] IPublishedConversationService service,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid projectId,
            Guid notebookId,
            Guid convoId) =>
        {
            var pubId = ctx.Request.Query["pubId"].ToString();
            if (!string.IsNullOrWhiteSpace(pubId) && Guid.TryParse(pubId, out var pubGuid))
            {
                var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
                var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
                var authResult = await authService.ValidateAsync(pubGuid, authHeader, projectId, notebookId, ctx.RequestAborted, apiKeyHeader, PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));

                if (!authResult.IsValid)
                {
                    var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

                    return Results.Json(
                        new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                        statusCode: statusCode);
                }

                SetPublishedChatUsageAttribution(ctx, pubGuid, authResult.UserIdentity);
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

            await service.UndoLastForConversationAsync(convoId);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        // POST external tool results (published)
        group.MapPost("/{convoId:guid}/tool-calls/results", async (
            HttpContext ctx,
            [FromServices] GuideAntsApi.DataModel.ApplicationDbContext db,
            [FromServices] IPublishedGuideAuthService authService,
            [FromServices] IPublishedConversationService publishedService,
            [FromServices] IPublishedGuideCostLimitService costLimits,
            Guid projectId,
            Guid notebookId,
            Guid convoId,
            [FromBody] List<ToolResultSubmission> results,
            [FromQuery] bool? resume) =>
        {
            var pubId = ctx.Request.Query["pubId"].ToString();
            if (string.IsNullOrWhiteSpace(pubId) || !Guid.TryParse(pubId, out var pubGuid))
            {
                return Results.BadRequest(new { error = "Missing or invalid 'pubId' query parameter." });
            }

            var authHeader = ctx.Request.Headers["X-Published-Auth"].ToString();
            var apiKeyHeader = ctx.Request.Headers[PublishedGuideAuthService.ApiKeyHeaderName].ToString();
            var authResult = await authService.ValidateAsync(pubGuid, authHeader, projectId, notebookId, ctx.RequestAborted, apiKeyHeader, PublishedGuideAuthService.ReadAppAuthCookie(ctx.Request));
            if (!authResult.IsValid)
            {
                var statusCode = PublishedGuideAuthService.MapValidationFailureStatusCode(authResult.ErrorCode);

                return Results.Json(
                    new { error = authResult.ErrorCode, message = authResult.ErrorMessage, requiresAuth = true },
                    statusCode: statusCode);
            }

            SetPublishedChatUsageAttribution(ctx, pubGuid, authResult.UserIdentity);

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

            // Validate conversation belongs to project/notebook
            var convo = await db.NotebookConversations
                .Include(c => c.Turns)
                .FirstOrDefaultAsync(c => c.Id == convoId, ctx.RequestAborted);
            if (convo == null || convo.NotebookId != notebookId)
            {
                return Results.NotFound();
            }

            var turn = convo.Turns.OrderByDescending(t => t.TurnIndex).FirstOrDefault();
            if (turn == null)
            {
                return Results.Conflict(new { error = "No active turn to append tool results." });
            }

            // Idempotency/dup checks (by toolCallId)
            var existingIds = await db.NotebookConversationMessages
                .Where(m => m.NotebookConversationId == convoId && m.TurnIndex == turn.TurnIndex && m.ToolCallId != null)
                .Select(m => m.ToolCallId!)
                .ToListAsync(ctx.RequestAborted);
            foreach (var r in results)
            {
                if (string.IsNullOrWhiteSpace(r.ToolCallId)) return Results.BadRequest(new { error = "toolCallId required" });
                if (existingIds.Any(id => string.Equals(id, r.ToolCallId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.Conflict(new { error = "duplicate_tool_call", toolCallId = r.ToolCallId });
                }
            }

            // Append tool results
            var now = DateTime.UtcNow;
            foreach (var r in results)
            {
                var m = new GuideAntsApi.DataModel.Models.NotebookConversationMessage
                {
                    NotebookConversationId = convoId,
                    TurnIndex = turn.TurnIndex,
                    MessageSequence = (await db.NotebookConversationMessages.Where(x => x.NotebookConversationId == convoId && x.TurnIndex == turn.TurnIndex).MaxAsync(x => (int?)x.MessageSequence, ctx.RequestAborted) ?? 1) + 1,
                    Role = ChatRole.Tool,
                    Content = r.Content ?? string.Empty,
                    ToolCallId = r.ToolCallId,
                    FunctionName = r.Name ?? "external_tool",
                    Created = now
                };
                db.NotebookConversationMessages.Add(m);
            }
            // Keep the turn in 'streaming' while server tools are being processed/resumed
            turn.Status = "streaming";
            turn.LastUpdated = now;
            await db.SaveChangesAsync(ctx.RequestAborted);

            // If resume requested, stream continuation
            if (resume == true)
            {
                ctx.Response.Headers["Content-Type"] = "text/event-stream";
                await foreach (var ev in publishedService.ResumeAfterExternalToolResultsStreamAsync(convoId, pubId, authResult.UserIdentity, authResult.InternalUserId, ctx.RequestAborted))
                {
                    await ctx.Response.WriteSseEventAsync(ev.EventType, ev.Payload, ctx.RequestAborted);
                }
                return Results.Empty;
            }

            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public record ToolResultSubmission
    {
        public string? ToolCallId { get; init; }
        public string? Name { get; init; }
        public string? Content { get; init; }
    }

    private static string DetectUploadType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tiff" or "webp" => "ImageFile",
            "wav" or "mp3" or "flac" or "aac" or "ogg" or "m4a" => "AudioFile",
            "txt" or "md" or "json" or "xml" or "csv" or "pdf" => "TextFile",
            _ => "SandboxFile"
        };
    }

    private static void SetPublishedChatUsageAttribution(HttpContext httpContext, Guid publishedGuideId, string? externalUserIdentity)
    {
        UsageAttributionHttpContext.Set(
            httpContext,
            new UsageAttributionContext(
                PublishedGuideId: publishedGuideId,
                SourceChannel: "published_chat",
                ExternalRequestId: UsageAttributionHttpContext.ResolveExternalRequestId(httpContext),
                ExternalUserIdentity: externalUserIdentity));
    }
}

