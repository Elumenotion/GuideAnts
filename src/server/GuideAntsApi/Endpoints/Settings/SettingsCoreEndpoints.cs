using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsCoreEndpoints
{
    public static void MapSettingsCoreEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        group.MapGet("/sections", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var sections = await settingsService.GetSectionSummariesAsync(cancellationToken);
            return Results.Ok(sections);
        })
        .WithName("GetSettingsSections")
        .Produces<IReadOnlyList<SettingsSectionSummaryDto>>(StatusCodes.Status200OK);

        group.MapGet("/schema", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var schema = await settingsService.GetSchemaAsync(cancellationToken);
            return Results.Ok(schema);
        })
        .WithName("GetSettingsSchema")
        .Produces<SettingsSchemaDto>(StatusCodes.Status200OK);

        group.MapGet("/readiness", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var readiness = await settingsService.GetReadinessAsync(cancellationToken);
            return Results.Ok(readiness);
        })
        .WithName("GetSettingsReadiness")
        .Produces<SettingsReadinessDto>(StatusCodes.Status200OK);

        group.MapGet("/sections/{sectionName}", async (
            string sectionName,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var section = await settingsService.GetSectionAsync(sectionName, cancellationToken);
            return section == null ? Results.NotFound() : Results.Ok(section);
        })
        .WithName("GetSettingsSection")
        .Produces<SettingsSectionDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/sections/{sectionName}", async (
            string sectionName,
            [FromBody] UpdateSettingsSectionRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var result = await settingsService.UpdateSectionAsync(sectionName, request, cancellationToken);
            if (result.ConcurrencyConflict)
            {
                return Results.Conflict(new { error = "Section was modified by another request. Refresh and try again." });
            }

            if (result.ValidationErrors.Count > 0)
            {
                return Results.BadRequest(new { errors = result.ValidationErrors });
            }

            return result.Section == null ? Results.NotFound() : Results.Ok(result.Section);
        })
        .WithName("UpdateSettingsSection")
        .Produces<SettingsSectionDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        const string chatDefaultsSectionName = "ChatDefaults";

        group.MapGet("/chat-defaults", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var section = await settingsService.GetSectionAsync(chatDefaultsSectionName, cancellationToken);
            return section is null
                ? Results.NotFound()
                : Results.Ok(SettingsChatDefaultsMapper.MapChatDefaults(section));
        })
        .WithName("GetChatDefaults")
        .Produces<ChatDefaultsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/chat-defaults", async (
            [FromBody] UpdateChatDefaultsRequest request,
            IApplicationSettingsService settingsService,
            GuideAntsApi.Services.Bootstrap.ILocalAiStartupWarmupService localAiWarmup,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var update = new UpdateSettingsSectionRequest(request.RowVersion, SettingsChatDefaultsMapper.BuildChatDefaultsPayload(request));
            var result = await settingsService.UpdateSectionAsync(chatDefaultsSectionName, update, cancellationToken);
            if (result.ConcurrencyConflict)
            {
                return Results.Conflict(new { error = "Section was modified by another request. Refresh and try again." });
            }

            if (result.ValidationErrors.Count > 0)
            {
                return Results.BadRequest(new { errors = result.ValidationErrors });
            }

            if (result.Section is null)
            {
                return Results.NotFound();
            }

            try
            {
                await localAiWarmup.WarmupAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("ChatDefaultsRuntimeReload")
                    .LogWarning(ex, "Failed to reload local AI stack after chat-defaults update.");
            }

            return Results.Ok(SettingsChatDefaultsMapper.MapChatDefaults(result.Section));
        })
        .WithName("UpdateChatDefaults")
        .Produces<ChatDefaultsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/embeddings/rebuild", async (
            IEmbeddingsRebuildService rebuildService,
            CancellationToken cancellationToken) =>
        {
            var result = await rebuildService.RequestRebuildAsync(cancellationToken);
            if (!result.Enqueued)
            {
                return Results.Conflict(new EmbeddingsRebuildConflictResponse(
                    Error: "An embeddings rebuild job is already pending or processing.",
                    JobId: result.JobId,
                    Status: result.Status));
            }

            return Results.Accepted(
                $"/api/settings/embeddings/rebuild/{result.JobId}",
                new EmbeddingsRebuildResponse(result.JobId, result.Status));
        })
        .WithName("RebuildEmbeddings")
        .Produces<EmbeddingsRebuildResponse>(StatusCodes.Status202Accepted)
        .Produces<EmbeddingsRebuildConflictResponse>(StatusCodes.Status409Conflict);
    }
}
