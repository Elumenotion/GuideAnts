using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Models.Conversations;

namespace GuideAntsApi.Endpoints;

public static class UserConversationsEndpoints
{
    public static void MapUserConversationsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/conversations")
            .WithTags("UserConversations");

        // Get user conversations across all projects with pagination, search, and sorting
        group.MapGet("/", async (
            [FromServices] IConversationService svc,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] string sortBy = "date",
            [FromQuery] string sortOrder = "desc") =>
        {
            var query = new UserConversationsQuery
            {
                Page = page,
                PageSize = pageSize,
                Search = search,
                SortBy = sortBy,
                SortOrder = sortOrder
            };
            
            var result = await svc.GetUserConversationsAsync(query);
            return Results.Ok(result);
        })
        .WithName("GetUserConversations")
        .Produces<PagedUserConversationsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }
}






