using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models;
using GuideAntsApi.Services;

namespace GuideAntsApi.Endpoints;

public static class WebScrapingEndpoints
{
    public static void MapWebScrapingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/web-scraping")
            .WithTags("Web Scraping")
            .WithOpenApi();

        group.MapGet("/content", async (
            [AsParameters] ContentRequestDto request,
            IWebScrapingService webScrapingService) =>
        {
            try
            {
                var content = await webScrapingService.GetPageContentAsync(request.Url);
                return Results.Ok(new ContentResponseDto(content));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                var readable = $"Page was not readable: {ex.Message}";
                return Results.Ok(new ContentResponseDto(readable));
            }
        })
        .WithName("GetContent")
        .Produces<ContentResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);
    }
} 
