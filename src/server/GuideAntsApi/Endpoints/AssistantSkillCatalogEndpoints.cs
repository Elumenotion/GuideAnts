using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Skills;

namespace GuideAntsApi.Endpoints;

/// <summary>
/// Assistant-scoped skill catalog actions: install from the store and update an installed
/// skill to a newer catalog version.
/// </summary>
public static class AssistantSkillCatalogEndpoints
{
    public static void MapAssistantSkillCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/assistants")
            .WithTags("Assistants")
            .RequireAuthorization("RequireAdmin")
            .WithOpenApi();

        group.MapPost("/{assistantId:guid}/skills/install", async (
            Guid assistantId,
            SkillInstallRequest request,
            ISkillInstallService install,
            CancellationToken ct) =>
        {
            try
            {
                var skill = await install.InstallAsync(assistantId, request, ct);
                return Results.Ok(skill);
            }
            catch (SkillInstallService.SkillConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message, skillName = ex.SkillName });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("InstallSkillFromStore")
        .Produces<AssistantSkillDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{assistantId:guid}/skills/{skillName}/update-from-store", async (
            Guid assistantId,
            string skillName,
            SkillUpdateFromStoreRequest? request,
            ISkillInstallService install,
            CancellationToken ct) =>
        {
            try
            {
                var skill = await install.UpdateFromCatalogAsync(
                    assistantId,
                    skillName,
                    request ?? new SkillUpdateFromStoreRequest(null),
                    ct);
                return Results.Ok(skill);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateSkillFromStore")
        .Produces<AssistantSkillDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);
    }
}
