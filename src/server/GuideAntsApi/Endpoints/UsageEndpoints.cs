using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models;
using GuideAntsApi.Services;

namespace GuideAntsApi.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/usage")
            .WithTags("Usage");

        group.MapGet("/summary", async (
            [FromServices] IUsageQueryService usage,
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            [FromQuery] string bucket,
            HttpContext http
        ) =>
        {
            var parsed = Enum.TryParse<UsageBucketDto>(bucket, true, out var bkt) ? bkt : UsageBucketDto.Day;
            var result = await usage.GetOwnerUsageSummaryAsync(from, to, parsed);
            return Results.Ok(result);
        });

        group.MapGet("/by-project", async (
            [FromServices] IUsageQueryService usage,
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            HttpContext http
        ) =>
        {
            var result = await usage.GetOwnerUsageByProjectAsync(from, to);
            return Results.Ok(result);
        });

        group.MapGet("/projects/{projectId:guid}/summary", async (
            [FromServices] IUsageQueryService usage,
            [FromRoute] Guid projectId,
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            [FromQuery] string bucket,
            HttpContext http
        ) =>
        {
            var parsed = Enum.TryParse<UsageBucketDto>(bucket, true, out var bkt) ? bkt : UsageBucketDto.Day;
            var result = await usage.GetProjectUsageSummaryAsync(projectId, from, to, parsed);
            return result is null ? Results.Forbid() : Results.Ok(result);
        });

        group.MapGet("/details", async (
            [FromServices] IUsageQueryService usage,
            [AsParameters] UsageDetailsQueryDto query,
            HttpContext http
        ) =>
        {
            var result = await usage.GetOwnerUsageDetailsAsync(query);
            return Results.Ok(result);
        });

        group.MapGet("/projects/{projectId:guid}/details", async (
            [FromServices] IUsageQueryService usage,
            [FromRoute] Guid projectId,
            [AsParameters] UsageDetailsQueryDto query,
            HttpContext http
        ) =>
        {
            var result = await usage.GetProjectUsageDetailsAsync(projectId, query);
            return result is null ? Results.Forbid() : Results.Ok(result);
        });

        group.MapGet("/breakdown", async (
            [FromServices] IUsageQueryService usage,
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            HttpContext http
        ) =>
        {
            var result = await usage.GetOwnerUsageBreakdownAsync(from, to, null);
            return Results.Ok(result);
        });

        group.MapGet("/projects/{projectId:guid}/breakdown", async (
            [FromServices] IUsageQueryService usage,
            [FromRoute] Guid projectId,
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            HttpContext http
        ) =>
        {
            var result = await usage.GetOwnerUsageBreakdownAsync(from, to, projectId);
            return Results.Ok(result);
        });
    }
}


