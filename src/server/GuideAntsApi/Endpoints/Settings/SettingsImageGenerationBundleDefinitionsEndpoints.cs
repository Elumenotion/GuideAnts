using System.Text.Json;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Settings;
using Microsoft.AspNetCore.Mvc;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsImageGenerationBundleDefinitionsEndpoints
{
    public static void MapSettingsImageGenerationBundleDefinitionsEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapServiceEditorsGroup(app);

        group.MapGet("/ImageGeneration/bundle-definitions", async (
            IApplicationSettingsService settings,
            CancellationToken cancellationToken) =>
        {
            var items = await settings.GetImageGenerationBundleDefinitionsAsync(cancellationToken);
            return Results.Ok(new ImageGenerationBundleDefinitionListDto(items));
        })
        .WithName("GetImageGenerationBundleDefinitions");

        group.MapGet("/ImageGeneration/bundle-definitions/{bundleId}", async (
            string bundleId,
            IApplicationSettingsService settings,
            CancellationToken cancellationToken) =>
        {
            var definition = await settings.GetImageGenerationBundleDefinitionAsync(bundleId, cancellationToken);
            return definition is null
                ? Results.NotFound(new { error = $"Bundle definition '{bundleId}' was not found." })
                : Results.Ok(definition);
        })
        .WithName("GetImageGenerationBundleDefinition");

        group.MapPut("/ImageGeneration/bundle-definitions/{bundleId}", async (
            string bundleId,
            [FromBody] ImageGenerationBundleDefinitionDto definition,
            IApplicationSettingsService settings,
            IBundleDefinitionProjectionService projectionService,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(bundleId, definition.BundleId, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Route bundleId must match definition.bundleId." });
            }

            var errors = BundleDefinitionValidator.Validate(definition);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { error = string.Join(' ', errors) });
            }

            var saved = await settings.UpsertImageGenerationBundleDefinitionAsync(definition, cancellationToken);
            await projectionService.ProjectBundleAsync(saved.BundleId, cancellationToken);
            return Results.Ok(saved);
        })
        .WithName("UpsertImageGenerationBundleDefinition");

        group.MapPost("/ImageGeneration/bundle-definitions/import", async (
            [FromBody] ImageGenerationBundleDefinitionImportRequest request,
            IApplicationSettingsService settings,
            IBundleDefinitionProjectionService projectionService,
            CancellationToken cancellationToken) =>
        {
            var validationError = ServiceLocalModelDownloadValidator.ValidateImportDefinition(request.Definition);
            if (validationError is not null)
            {
                return validationError;
            }

            var saved = await settings.UpsertImageGenerationBundleDefinitionAsync(request.Definition, cancellationToken);
            await projectionService.ProjectBundleAsync(saved.BundleId, cancellationToken);
            return Results.Ok(saved);
        })
        .WithName("ImportImageGenerationBundleDefinition");

        group.MapGet("/ImageGeneration/bundle-definitions/{bundleId}/export", async (
            string bundleId,
            IApplicationSettingsService settings,
            CancellationToken cancellationToken) =>
        {
            var definition = await settings.GetImageGenerationBundleDefinitionAsync(bundleId, cancellationToken);
            if (definition is null)
            {
                return Results.NotFound(new { error = $"Bundle definition '{bundleId}' was not found." });
            }

            return Results.Json(
                definition,
                BundleDefinitionJson.Options);
        })
        .WithName("ExportImageGenerationBundleDefinition");
    }

    public static ImageGenerationBundleDefinitionDto? TryMapDownloadPayloadToDefinition(JsonElement payload)
    {
        if (!LocalServiceAdminRouting.TryGetNonEmptyString(payload, "bundle_id", out var bundleId))
        {
            return null;
        }

        if (!LocalServiceAdminRouting.TryGetNonEmptyString(payload, "diffusion_repo", out var diffusionRepo)
            || !LocalServiceAdminRouting.TryGetNonEmptyString(payload, "diffusion_file", out var diffusionFile)
            || !LocalServiceAdminRouting.TryGetNonEmptyString(payload, "vae_repo", out var vaeRepo)
            || !LocalServiceAdminRouting.TryGetNonEmptyString(payload, "vae_file", out var vaeFile)
            || !LocalServiceAdminRouting.TryGetNonEmptyString(payload, "text_encoder_repo", out var textEncoderRepo)
            || !LocalServiceAdminRouting.TryGetNonEmptyString(payload, "text_encoder_file", out var textEncoderFile)
            || !LocalServiceAdminRouting.TryGetNonEmptyString(payload, "sampling_method", out var samplingMethod))
        {
            return null;
        }

        if (!ServiceLocalModelDownloadValidator.TryGetPositiveInt(payload, "sampling_steps", out var samplingSteps)
            || !ServiceLocalModelDownloadValidator.TryGetPositiveDouble(payload, "sampling_cfg_scale", out var samplingCfgScale))
        {
            return null;
        }

        payload.TryGetProperty("revision", out var revisionElement);
        var revision = revisionElement.ValueKind == JsonValueKind.String
            ? revisionElement.GetString()
            : null;

        return new ImageGenerationBundleDefinitionDto(
            bundleId,
            revision,
            UpdatedAtUtc: null,
            new BundleDefinitionRolesDto(
                new BundleDefinitionRoleDto(diffusionRepo, diffusionFile),
                new BundleDefinitionRoleDto(vaeRepo, vaeFile),
                new BundleDefinitionRoleDto(textEncoderRepo, textEncoderFile)),
            new BundleDefinitionSamplingDto(samplingSteps, samplingCfgScale, samplingMethod));
    }
}
