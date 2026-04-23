using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Auth;

namespace GuideAntsApi.Endpoints;

public static class GuidesPublishingEndpoints
{
    /// <summary>
    /// Maps a PublishedGuide entity to a PublishedGuideDto.
    /// </summary>
    private static PublishedGuideDto ToDto(PublishedGuide pg, Guid projectId)
    {
        return new PublishedGuideDto
        {
            Id = pg.Id,
            GuideId = pg.GuideId,
            GuideName = pg.Guide?.Name ?? string.Empty,
            NotebookId = pg.NotebookId,
            ProjectId = projectId,
            Created = pg.Created,
            Active = pg.Active,
            RetentionPeriod = pg.RetentionPeriod,
            MaxUserMessageLength = pg.MaxUserMessageLength,
            MaxTurns = pg.MaxTurns,
            DailyChargeLimitUsd = pg.DailyChargeLimitUsd,
            BillingPeriodChargeLimitUsd = pg.BillingPeriodChargeLimitUsd,
            AuthValidationWebhookUrl = pg.AuthValidationWebhookUrl,
            AuthWebhookTimeoutSeconds = pg.AuthWebhookTimeoutSeconds,
            FriendlyName = pg.FriendlyName,
            DisplayMode = pg.DisplayMode,
            CommandMode = pg.CommandMode,
            ShowTurnNavigation = pg.ShowTurnNavigation,
            Collapsible = pg.Collapsible,
            ShowConversationStarters = pg.ShowConversationStarters,
            ShowAttachments = pg.ShowAttachments,
            HasApiKey = !string.IsNullOrWhiteSpace(pg.ApiKeyHash)
        };
    }

    public static void MapGuidesPublishingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/guides/{guideId}/publish")
            .WithTags("Guides Publishing")
            .WithOpenApi();

        // Validate friendly name availability
        group.MapGet("/validate-friendly-name/{friendlyName}", async (
            Guid guideId,
            string friendlyName,
            [FromServices] ApplicationDbContext db) =>
        {
            
            var ownsGuide = await db.Assistants.AsNoTracking()
                .AnyAsync(a => a.Id == guideId && a.Kind == AssistantKind.Guide);
            if (!ownsGuide)
                return Results.Forbid();

            // Validate URL safety
            try
            {
                var encoded = Uri.EscapeDataString(friendlyName);
                if (encoded != friendlyName)
                {
                    return Results.Ok(new { 
                        available = false, 
                        reason = "Friendly name contains invalid characters for URL" 
                    });
                }
            }
            catch
            {
                return Results.Ok(new { 
                    available = false, 
                    reason = "Friendly name is not valid for URL" 
                });
            }

            // Check uniqueness (case-insensitive)
            var exists = await db.PublishedGuides
                .Where(pg => pg.FriendlyName != null && 
                             pg.FriendlyName.ToLower() == friendlyName.ToLower())
                .AnyAsync();

            return Results.Ok(new { 
                available = !exists,
                reason = exists ? "Friendly name is already taken" : null
            });
        })
        .WithName("ValidateFriendlyName")
        .Produces<object>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Publish a guide to a project
        group.MapPost("", async (
            Guid guideId,
            [FromBody] PublishGuideDto dto,
            [FromServices] ApplicationDbContext db,
            [FromServices] INotebookService notebookService) =>
        {
            

            // Verify project access (must be contributor to publish)

var guide = await db.Assistants
                .Where(a => a.Id == guideId && a.Kind == AssistantKind.Guide)
                .FirstOrDefaultAsync();

            if (guide == null)
                return Results.NotFound(new { error = "Guide not found or you do not own this guide" });

            // Validate friendly name if provided
            if (!string.IsNullOrWhiteSpace(dto.FriendlyName))
            {
                // Validate URL safety - ensure it can be used in a URL path segment
                try
                {
                    // Test if the friendly name is valid for URL (no encoding needed)
                    var encoded = Uri.EscapeDataString(dto.FriendlyName);
                    if (encoded != dto.FriendlyName)
                    {
                        return Results.BadRequest(new { 
                            error = "Friendly name contains invalid characters for URL",
                            field = "friendlyName"
                        });
                    }
                }
                catch
                {
                    return Results.BadRequest(new { 
                        error = "Friendly name is not valid for URL",
                        field = "friendlyName"
                    });
                }
                
                // Case-insensitive uniqueness check
                var existingFriendlyName = await db.PublishedGuides
                    .Where(pg => pg.FriendlyName != null && 
                                 pg.FriendlyName.ToLower() == dto.FriendlyName.ToLower())
                    .AnyAsync();
                
                if (existingFriendlyName)
                {
                    return Results.Conflict(new { 
                        error = "Friendly name is already taken",
                        field = "friendlyName"
                    });
                }
            }

            // Validate cost limits (must be non-negative when provided)
            if (dto.DailyChargeLimitUsd.HasValue && dto.DailyChargeLimitUsd.Value < 0m)
            {
                return Results.BadRequest(new { error = "Daily cost limit must be >= 0", field = "dailyChargeLimitUsd" });
            }
            if (dto.BillingPeriodChargeLimitUsd.HasValue && dto.BillingPeriodChargeLimitUsd.Value < 0m)
            {
                return Results.BadRequest(new { error = "Billing period cost limit must be >= 0", field = "billingPeriodChargeLimitUsd" });
            }

            // Check if already published (same guide + project)
            var existingPublish = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .Where(pg => pg.GuideId == guideId && pg.Notebook.ProjectId == dto.ProjectId)
                .FirstOrDefaultAsync();

            if (existingPublish != null)
                return Results.Conflict(new { error = "Guide is already published in this project", publishedGuideId = existingPublish.Id });

            // Create notebook
            var notebook = await notebookService.CreateAsync(
                dto.ProjectId,
                new CreateNotebookDto
                {
                    Title = $"Published {guide.Name}",
                    Description = $"Published guide notebook for {guide.Name}",
                    GuideId = guideId
                });

            // Create PublishedGuide
            var publishedGuide = new PublishedGuide
            {
                GuideId = guideId,
                NotebookId = notebook.Id,
                Created = DateTime.UtcNow,
                Active = true,
                RetentionPeriod = dto.RetentionPeriod,
                MaxUserMessageLength = dto.MaxUserMessageLength,
                MaxTurns = dto.MaxTurns,
                DailyChargeLimitUsd = dto.DailyChargeLimitUsd,
                BillingPeriodChargeLimitUsd = dto.BillingPeriodChargeLimitUsd,
                AuthValidationWebhookUrl = dto.AuthValidationWebhookUrl,
                AuthWebhookTimeoutSeconds = dto.AuthWebhookTimeoutSeconds,
                FriendlyName = dto.FriendlyName,
                DisplayMode = dto.DisplayMode ?? "full",
                CommandMode = dto.CommandMode,
                ShowTurnNavigation = dto.ShowTurnNavigation,
                Collapsible = dto.Collapsible,
                ShowConversationStarters = dto.ShowConversationStarters,
                ShowAttachments = dto.ShowAttachments
            };

            db.PublishedGuides.Add(publishedGuide);
            await db.SaveChangesAsync();

            // Set guide reference for DTO mapping
            publishedGuide.Guide = guide;
            var result = ToDto(publishedGuide, dto.ProjectId);

            return Results.Created($"/api/guides/{guideId}/publish/{publishedGuide.Id}", result);
        })
        .WithName("PublishGuide")
        .Produces<PublishedGuideDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        // Get published guide status in a specific project
        group.MapGet("/in-project/{projectId}", async (
            Guid guideId,
            Guid projectId,
            [FromServices] ApplicationDbContext db) =>
        {
            // Verify project access

var publishedGuide = await db.PublishedGuides
                .Include(pg => pg.Guide)
                .Include(pg => pg.Notebook)
                .Where(pg => pg.GuideId == guideId && pg.Notebook.ProjectId == projectId)
                .OrderByDescending(pg => pg.Created)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
                return Results.Content("null", "application/json"); // Not published in this project

            return Results.Ok(ToDto(publishedGuide, publishedGuide.Notebook.ProjectId));
        })
        .WithName("GetPublishedGuideInProject")
        .Produces<PublishedGuideDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Update published guide configuration
        group.MapPut("/{pubId}", async (
            Guid guideId,
            Guid pubId,
            [FromBody] UpdatePublishedGuideDto dto,
            [FromServices] ApplicationDbContext db) =>
        {
            var publishedGuide = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .Include(pg => pg.Guide)
                .Where(pg => pg.Id == pubId && pg.GuideId == guideId)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
                return Results.NotFound();

            // Verify project access

// Validate friendly name if provided and changed
            if (!string.IsNullOrWhiteSpace(dto.FriendlyName) && dto.FriendlyName != publishedGuide.FriendlyName)
            {
                // Validate URL safety
                try
                {
                    var encoded = Uri.EscapeDataString(dto.FriendlyName);
                    if (encoded != dto.FriendlyName)
                    {
                        return Results.BadRequest(new { 
                            error = "Friendly name contains invalid characters for URL",
                            field = "friendlyName"
                        });
                    }
                }
                catch
                {
                    return Results.BadRequest(new { 
                        error = "Friendly name is not valid for URL",
                        field = "friendlyName"
                    });
                }
                
                // Case-insensitive uniqueness check (excluding current record)
                var existingFriendlyName = await db.PublishedGuides
                    .Where(pg => pg.Id != pubId && 
                                 pg.FriendlyName != null && 
                                 pg.FriendlyName.ToLower() == dto.FriendlyName.ToLower())
                    .AnyAsync();
                
                if (existingFriendlyName)
                {
                    return Results.Conflict(new { 
                        error = "Friendly name is already taken",
                        field = "friendlyName"
                    });
                }
            }

            // Validate mutual exclusivity: cannot set webhook URL if API key is configured
            if (!string.IsNullOrWhiteSpace(dto.AuthValidationWebhookUrl) && 
                !string.IsNullOrWhiteSpace(publishedGuide.ApiKeyHash))
            {
                return Results.BadRequest(new { 
                    error = "Cannot set webhook authentication while API key authentication is enabled. Remove the API key first.",
                    field = "authValidationWebhookUrl"
                });
            }

            // Validate cost limits (must be non-negative when provided)
            if (dto.DailyChargeLimitUsd.HasValue && dto.DailyChargeLimitUsd.Value < 0m)
            {
                return Results.BadRequest(new { error = "Daily cost limit must be >= 0", field = "dailyChargeLimitUsd" });
            }
            if (dto.BillingPeriodChargeLimitUsd.HasValue && dto.BillingPeriodChargeLimitUsd.Value < 0m)
            {
                return Results.BadRequest(new { error = "Billing period cost limit must be >= 0", field = "billingPeriodChargeLimitUsd" });
            }

            // Update fields
            publishedGuide.RetentionPeriod = dto.RetentionPeriod;
            publishedGuide.MaxUserMessageLength = dto.MaxUserMessageLength;
            publishedGuide.MaxTurns = dto.MaxTurns;
            publishedGuide.DailyChargeLimitUsd = dto.DailyChargeLimitUsd;
            publishedGuide.BillingPeriodChargeLimitUsd = dto.BillingPeriodChargeLimitUsd;
            publishedGuide.AuthValidationWebhookUrl = dto.AuthValidationWebhookUrl;
            publishedGuide.AuthWebhookTimeoutSeconds = dto.AuthWebhookTimeoutSeconds;
            publishedGuide.FriendlyName = dto.FriendlyName;
            publishedGuide.DisplayMode = dto.DisplayMode ?? "full";
            publishedGuide.CommandMode = dto.CommandMode;
            publishedGuide.ShowTurnNavigation = dto.ShowTurnNavigation;
            publishedGuide.Collapsible = dto.Collapsible;
            publishedGuide.ShowConversationStarters = dto.ShowConversationStarters;
            publishedGuide.ShowAttachments = dto.ShowAttachments;

            await db.SaveChangesAsync();

            return Results.Ok(ToDto(publishedGuide, publishedGuide.Notebook.ProjectId));
        })
        .WithName("UpdatePublishedGuide")
        .Produces<PublishedGuideDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        // Deactivate published guide
        group.MapPost("/{pubId}/deactivate", async (
            Guid guideId,
            Guid pubId,
            [FromServices] ApplicationDbContext db) =>
        {
            var publishedGuide = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .Where(pg => pg.Id == pubId && pg.GuideId == guideId)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
                return Results.NotFound();

            // Verify project access

publishedGuide.Active = false;
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("DeactivatePublishedGuide")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        // Reactivate published guide (recreate notebook if needed)
        group.MapPost("/{pubId}/reactivate", async (
            Guid guideId,
            Guid pubId,
            [FromServices] ApplicationDbContext db,
            [FromServices] INotebookService notebookService) =>
        {
            var publishedGuide = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .Include(pg => pg.Guide)
                .Where(pg => pg.Id == pubId && pg.GuideId == guideId)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
                return Results.NotFound();

            // Verify project access

// Check if notebook still exists
            var notebookExists = await db.Notebooks.AnyAsync(n => n.Id == publishedGuide.NotebookId);
            
            if (!notebookExists)
            {
                // Recreate notebook
                var notebook = await notebookService.CreateAsync(
                    publishedGuide.Notebook.ProjectId,
                    new CreateNotebookDto
                    {
                        Title = $"Published {publishedGuide.Guide.Name}",
                        Description = $"Published guide notebook for {publishedGuide.Guide.Name}",
                        GuideId = guideId
                    });

                publishedGuide.NotebookId = notebook.Id;
            }

            publishedGuide.Active = true;
            await db.SaveChangesAsync();

            return Results.Ok(ToDto(publishedGuide, publishedGuide.Notebook.ProjectId));
        })
        .WithName("ReactivatePublishedGuide")
        .Produces<PublishedGuideDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        // Generate or regenerate API key for published guide
        group.MapPost("/{pubId}/api-key", async (
            Guid guideId,
            Guid pubId,
            [FromServices] ApplicationDbContext db) =>
        {
            var publishedGuide = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .Where(pg => pg.Id == pubId && pg.GuideId == guideId)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
                return Results.NotFound();

            // Verify project access (must be contributor)

// API key and webhook are mutually exclusive
            if (!string.IsNullOrWhiteSpace(publishedGuide.AuthValidationWebhookUrl))
            {
                return Results.BadRequest(new { 
                    error = "Cannot enable API key authentication while webhook authentication is configured. Remove the webhook URL first.",
                    field = "apiKey"
                });
            }

            // Generate new API key
            var plainApiKey = PublishedGuideAuthService.GenerateApiKey();
            var hashedApiKey = PublishedGuideAuthService.HashApiKey(plainApiKey);

            publishedGuide.ApiKeyHash = hashedApiKey;
            await db.SaveChangesAsync();

            return Results.Ok(new ApiKeyGenerationResultDto
            {
                ApiKey = plainApiKey,
                Warning = "This API key will only be shown once. Store it securely - you will not be able to retrieve it again."
            });
        })
        .WithName("GeneratePublishedGuideApiKey")
        .Produces<ApiKeyGenerationResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);

        // Remove API key from published guide
        group.MapDelete("/{pubId}/api-key", async (
            Guid guideId,
            Guid pubId,
            [FromServices] ApplicationDbContext db) =>
        {
            var publishedGuide = await db.PublishedGuides
                .Include(pg => pg.Notebook)
                .Where(pg => pg.Id == pubId && pg.GuideId == guideId)
                .FirstOrDefaultAsync();

            if (publishedGuide == null)
                return Results.NotFound();

            // Verify project access (must be contributor)

publishedGuide.ApiKeyHash = null;
            await db.SaveChangesAsync();

            return Results.NoContent();
        })
        .WithName("RemovePublishedGuideApiKey")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }
}

