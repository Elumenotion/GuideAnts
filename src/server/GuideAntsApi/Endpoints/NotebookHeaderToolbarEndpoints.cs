using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models;
using GuideAntsApi.Services.NotebookHeaderToolbar;

namespace GuideAntsApi.Endpoints;

public static class NotebookHeaderToolbarEndpoints
{
    public static void MapNotebookHeaderToolbarEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/notebooks/{notebookId:guid}/header-toolbar")
            .WithTags("NotebookHeaderToolbar");

        group.MapGet("/", async (
            [FromServices] INotebookHeaderToolbarService toolbar,
            Guid notebookId,
            [FromQuery] Guid? conversationId,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var dto = await toolbar.GetToolbarAsync(notebookId, conversationId, cancellationToken);
                return Results.Ok(dto);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
        .Produces<NotebookHeaderToolbarDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
    }
}
