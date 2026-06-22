using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Guides;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Endpoints;

public static class GuidesEndpoints
{
    private sealed class LogCategory;

    public static void MapGuidesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/guides")
            .WithTags("Guides")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        // List guides
        group.MapGet("/", async (Guid? projectId, IGuidesService guidesService) =>
        {
            var guides = await guidesService.GetGuidesAsync(projectId);
            return Results.Ok(guides);
        })
        .WithName("GetGuides")
        .Produces<IEnumerable<GuideDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Get single guide
        group.MapGet("/{guideId}", async (Guid guideId, Guid? projectId, IGuidesService guidesService) =>
        {
            var guide = await guidesService.GetGuideAsync(guideId, projectId);
            if (guide == null)
                return Results.NotFound();

            return Results.Ok(guide);
        })
        .WithName("GetGuide")
        .Produces<GuideDetailsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Validate guide runtime compatibility
        group.MapPost("/runtime/validate", async (GuideRuntimeValidationRequest request, IGuidesService guidesService) =>
        {
            var result = await guidesService.ValidateRuntimeCompatibilityAsync(request);
            return Results.Ok(result);
        })
        .WithName("ValidateGuideRuntime")
        .Produces<GuideRuntimeValidationDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Create guide
        group.MapPost("/", async (CreateGuideDto dto, IGuidesService guidesService, ILogger<LogCategory> logger) =>
        {
            try
            {
                var guide = await guidesService.CreateGuideAsync(dto);
                return Results.Created($"/api/guides/{guide.Id}", guide);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "CreateGuide failed");
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateGuide")
        .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue))
        .Produces<GuideDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Update guide
        group.MapPut("/{guideId}", async (Guid guideId, UpdateGuideDto dto, IGuidesService guidesService, ILogger<LogCategory> logger) =>
        {
            try
            {
                var guide = await guidesService.UpdateGuideAsync(guideId, dto);
                return Results.Ok(guide);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "UpdateGuide failed for guide {GuideId}", guideId);
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateGuide")
        .WithMetadata(new RequestSizeLimitAttribute(long.MaxValue))
        .Produces<GuideDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Delete guide
        group.MapDelete("/{guideId}", async (Guid guideId, IGuidesService guidesService) =>
        {
            var result = await guidesService.DeleteGuideAsync(guideId);
            if (!result)
                return Results.NotFound();

            return Results.NoContent();
        })
        .WithName("DeleteGuide")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Duplicate guide
        group.MapPost("/{guideId}/duplicate", async (Guid guideId, IGuidesService guidesService) =>
        {
            try
            {
                var guide = await guidesService.DuplicateAssistantAsync(guideId);
                return Results.Created($"/api/guides/{guide.Id}", guide);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("DuplicateGuide")
        .Produces<GuideDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Export guide
        group.MapGet("/{guideId}/export", async (Guid guideId, IGuideExportImportService exportImportService) =>
        {
            try
            {
                var zipBytes = await exportImportService.ExportGuideAsync(guideId);
                var fileName = $"guide-{guideId}-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
                return Results.File(zipBytes, "application/zip", fileName);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ExportGuide")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Import guide
        group.MapPost("/import", async (IFormFile file, IGuideExportImportService exportImportService) =>
        {
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "File must be a .zip archive" });

            try
            {
                using var stream = file.OpenReadStream();
                var result = await exportImportService.ImportGuideAsync(stream);
                return Results.Ok(result);
            }
            catch (NotImplementedException)
            {
                return Results.BadRequest(new { error = "Import functionality is not yet implemented" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ImportGuide")
        .Produces<ImportGuideResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .DisableAntiforgery();  // Required for file uploads

        // Get guide avatar
        group.MapGet("/{guideId}/avatar", async (Guid guideId, IGuidesService guidesService, HttpContext context) =>
        {
            var avatarBytes = await guidesService.GetGuideAvatarBytesAsync(guideId);
            if (avatarBytes == null || avatarBytes.Length == 0)
                return Results.NotFound();

            // Detect content type from bytes or default to PNG
            var contentType = DetectImageContentType(avatarBytes);
            
            // Add caching headers
            context.Response.Headers.CacheControl = "public, max-age=3600";
            
            return Results.File(avatarBytes, contentType);
        })
        .WithName("GetGuideAvatar")
        .Produces(StatusCodes.Status200OK, contentType: "image/png")
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // MCP tool source discovery (authoring only — not the published-guide MCP server)
        var mcpToolSourceGroup = app.MapGroup("/api/guides/tool-sources/mcp")
            .WithTags("MCP Tool Sources")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        mcpToolSourceGroup.MapPost("/test-connection", async (
            McpTestConnectionRequest request,
            IMcpToolSourceDiscoveryService discoveryService,
            CancellationToken cancellationToken) =>
        {
            var result = await discoveryService.TestConnectionAsync(request, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("TestMcpToolSourceConnection")
        .Produces<McpTestConnectionResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        mcpToolSourceGroup.MapPost("/discover", async (
            McpDiscoverToolsRequest request,
            IMcpToolSourceDiscoveryService discoveryService,
            CancellationToken cancellationToken) =>
        {
            var result = await discoveryService.DiscoverToolsAsync(request, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("DiscoverMcpToolSourceTools")
        .Produces<McpDiscoverToolsResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // OpenAPI Operations endpoints
        var operationsGroup = app.MapGroup("/api/operations")
            .WithTags("OpenAPI Operations")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        // Get single operation
        operationsGroup.MapGet("/{operationId}", async (Guid operationId, IGuidesService guidesService) =>
        {
            var operation = await guidesService.GetOperationAsync(operationId);
            if (operation == null)
                return Results.NotFound();

            return Results.Ok(operation);
        })
        .WithName("GetOperation")
        .Produces<OpenApiOperationDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Update operation
        operationsGroup.MapPut("/{operationId}", async (Guid operationId, UpdateOperationDto dto, IGuidesService guidesService) =>
        {
            try
            {
                var operation = await guidesService.UpdateOperationAsync(operationId, dto);
                return Results.Ok(operation);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateOperation")
        .Produces<OpenApiOperationDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        // Preview tool definition
        operationsGroup.MapPost("/preview", async (PreviewToolDefinitionDto dto, IGuidesService guidesService) =>
        {
            try
            {
                var preview = await guidesService.PreviewToolDefinitionAsync(dto);
                return Results.Ok(preview);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("PreviewToolDefinition")
        .Produces<ToolDefinitionPreviewResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }

    internal static string DetectImageContentType(byte[] imageBytes)
    {
        if (imageBytes.Length < 4)
            return "image/png";

        // PNG: 89 50 4E 47
        if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
            return "image/png";

        // JPEG: FF D8 FF
        if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8 && imageBytes[2] == 0xFF)
            return "image/jpeg";

        // GIF: 47 49 46
        if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46)
            return "image/gif";

        // WebP: 52 49 46 46 ... 57 45 42 50
        if (imageBytes.Length >= 12 && 
            imageBytes[0] == 0x52 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46 && imageBytes[3] == 0x46 &&
            imageBytes[8] == 0x57 && imageBytes[9] == 0x45 && imageBytes[10] == 0x42 && imageBytes[11] == 0x50)
            return "image/webp";

        // SVG: < s v g or < ? x m l (text-based)
        if (imageBytes[0] == 0x3C) // '<'
            return "image/svg+xml";

        // Default to PNG
        return "image/png";
    }
}
