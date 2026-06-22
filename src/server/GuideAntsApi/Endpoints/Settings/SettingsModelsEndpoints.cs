using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Configuration;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsModelsEndpoints
{
    public static void MapSettingsModelsEndpoints(this WebApplication app)
    {
        var group = SettingsGroupFactory.MapCoreGroup(app);

        group.MapGet("/models", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var models = await settingsService.GetModelsAsync(cancellationToken);
            return Results.Ok(models);
        })
        .WithName("GetSettingsModels")
        .Produces<IReadOnlyList<SettingsModelDto>>(StatusCodes.Status200OK);

        group.MapPost("/models", async (
            [FromBody] CreateSettingsModelRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await settingsService.CreateModelAsync(request, cancellationToken);
                return Results.Created($"/api/settings/models/{created.ModelId}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateSettingsModel")
        .Produces<SettingsModelDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest);

        // Canonical onboarding write API for both Settings and Home Wizard local model flows.
        group.MapPost("/models:add", async (
            [FromBody] AddModelRequest request,
            IApplicationSettingsService settingsService,
            IChatTargetValidator chatTargetValidator,
            IRuntimeProfileResolver runtimeProfileResolver,
            ILocalModelOnboardingValidator localModelOnboardingValidator,
            ILocalModelOnboardingOrchestrator localModelOnboardingOrchestrator,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var provider = request.Provider.Trim();
                if (string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
                {
                    var command = LocalModelOnboardingCommand.FromAddModelRequest(request);
                    var logger = loggerFactory.CreateLogger("LocalModelOnboarding");
                    logger.LogInformation(
                        "Local model onboarding request. ui={OnboardingUi} source={InstallSource} catalogModelId={CatalogModelId} routerModelId={RouterModelId}",
                        LogValueSanitizer.Sanitize(string.IsNullOrWhiteSpace(command.OnboardingUi) ? "unknown" : command.OnboardingUi),
                        LogValueSanitizer.Sanitize(command.InstallSource),
                        LogValueSanitizer.Sanitize(command.CatalogModelId),
                        LogValueSanitizer.Sanitize(command.RouterModelId));

                    await localModelOnboardingValidator.ValidateAsync(request, command, cancellationToken).ConfigureAwait(false);
                    var onboardingResult = await localModelOnboardingOrchestrator
                        .OnboardAsync(request, command, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(onboardingResult.ToResponse());
                }

                await SettingsModelOnboardingSupport.ValidateAddModelRequestAsync(
                    request,
                    settingsService,
                    chatTargetValidator,
                    cancellationToken).ConfigureAwait(false);

                var cloudReasoningChoicesJson = await SettingsModelOnboardingSupport.DeriveCloudReasoningChoicesJsonAsync(
                    runtimeProfileResolver,
                    request,
                    cancellationToken).ConfigureAwait(false);

                var created = await settingsService.CreateModelAsync(
                    SettingsModelOnboardingSupport.BuildModelCreateRequest(
                        request,
                        cloudReasoningChoicesJson,
                        runtimeConfigJson: SettingsModelOnboardingSupport.BuildCloudRuntimeConfigJson(request)),
                    cancellationToken).ConfigureAwait(false);

                return Results.Ok(new AddModelResponse(
                    OperationId: null,
                    AddOperation: new AddModelOperationDto(
                        Kind: "sync",
                        CatalogModel: created,
                        Status: "completed",
                        Error: null)));
            }
            catch (AddModelException ex)
            {
                return Results.BadRequest(ex.ToDto());
            }
            catch (RoutingException ex)
            {
                return Results.BadRequest(SettingsModelOnboardingSupport.MapAddModelRoutingError(ex));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new AddModelErrorDto(
                    Code: "INSTALL_STEP_FAILED",
                    Step: "validation",
                    Message: ex.Message,
                    Remediation: "Review the model details and try again."));
            }
        })
        .WithName("AddSettingsModel")
        .Produces<AddModelResponse>(StatusCodes.Status200OK)
        .Produces<AddModelErrorDto>(StatusCodes.Status400BadRequest);

        group.MapPut("/models/{**modelId}", async (
            string modelId,
            [FromBody] UpdateSettingsModelRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var normalizedModelId = SettingsModelOnboardingSupport.NormalizeRouteModelId(modelId);
                var updated = await settingsService.UpdateModelAsync(normalizedModelId, request, cancellationToken);
                return updated == null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateSettingsModel")
        .Produces<SettingsModelDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/models/{**modelId}", async (
            string modelId,
            ILlamaRuntimeInventoryService inventoryService,
            ILlamaServerRuntimeClient llamaClient,
            ILlamaRuntimeCoordinator coordinator,
            ILlamaRuntimeAdminClient adminClient,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var normalizedModelId = SettingsModelOnboardingSupport.NormalizeRouteModelId(modelId);
                var models = await settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
                var model = models.FirstOrDefault(m => string.Equals(m.ModelId, normalizedModelId, StringComparison.Ordinal));
                if (model is null)
                {
                    return Results.NotFound();
                }

                if (SettingsLlamaRouterDeleteHandler.TryGetLlamaRouterModelId(model, out var routerModelId))
                {
                    return await SettingsLlamaRouterDeleteHandler.DeleteLlamaRouterEntryAsync(
                        routerModelId,
                        inventoryService,
                        llamaClient,
                        coordinator,
                        adminClient,
                        settingsService,
                        cancellationToken).ConfigureAwait(false);
                }

                var deleted = await settingsService.DeleteModelAsync(normalizedModelId, cancellationToken);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (DbUpdateException ex)
            {
                return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
            }
        })
        .WithName("DeleteSettingsModel")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway);
    }
}
