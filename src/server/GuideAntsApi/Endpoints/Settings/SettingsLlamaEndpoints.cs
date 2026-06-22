using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsLlamaEndpoints
{
    public static void MapSettingsLlamaEndpoints(this WebApplication app)
    {
        var llamaGroup = SettingsGroupFactory.MapLlamaGroup(app);

        llamaGroup.MapGet("/runtime/inventory", async (
            IConfiguration configuration,
            ILlamaRuntimeInventoryService inventoryService,
            CancellationToken cancellationToken) =>
        {
            if (!SettingsGroupFactory.HasConfiguredLlamaRuntime(configuration))
            {
                return SettingsGroupFactory.LlamaRuntimeUnavailable();
            }

            var items = await inventoryService.GetInventoryAsync(cancellationToken);
            return Results.Ok(items);
        })
        .WithName("GetLlamaRuntimeInventory")
        .Produces<IReadOnlyList<LlamaRuntimeInventoryItemDto>>(StatusCodes.Status200OK);

        llamaGroup.MapPost("/runtime/load", async (
            [FromBody] LlamaRuntimeLoadRequest request,
            IConfiguration configuration,
            ILlamaServerRuntimeClient llamaClient,
            ILlamaRuntimeCoordinator coordinator,
            GuideAntsApi.Services.Bootstrap.ILocalAiStartupWarmupService localAiWarmup,
            CancellationToken cancellationToken) =>
        {
            if (!SettingsGroupFactory.HasConfiguredLlamaRuntime(configuration))
            {
                return SettingsGroupFactory.LlamaRuntimeUnavailable();
            }

            var handle = coordinator.TryAcquireAliasLock(request.RouterModelId);
            if (handle == null)
            {
                var problem = new ProblemDetails
                {
                    Type = $"{RoutingProblemDetailsFactory.ProblemTypeBase}runtime-not-ready",
                    Title = "Local runtime busy",
                    Status = StatusCodes.Status409Conflict,
                    Detail = $"A load or unload operation is already in progress for alias '{request.RouterModelId}'."
                };
                problem.Extensions["code"] = RoutingErrorCodes.RuntimeNotReady;
                problem.Extensions["action"] =
                    "Wait for the in-flight operation on this alias to complete, then retry.";
                problem.Extensions["modelId"] = request.RouterModelId;
                return Results.Problem(problem);
            }

            await using var _ = handle;
            var auxiliaryServicesWereUnloaded = false;
            try
            {
                await localAiWarmup.UnloadAuxiliaryServicesAsync(cancellationToken).ConfigureAwait(false);
                auxiliaryServicesWereUnloaded = true;

                await llamaClient.LoadModelAsync(request.RouterModelId, loadParams: null, cancellationToken);

                await localAiWarmup.EnsureAuxiliaryServicesLoadedAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                if (auxiliaryServicesWereUnloaded)
                {
                    try
                    {
                        await localAiWarmup.EnsureAuxiliaryServicesLoadedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original runtime load failure.
                    }
                }

                return Results.Problem(ex.Message);
            }
        })
        .WithName("LoadLlamaRuntimeModel")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        llamaGroup.MapPost("/runtime/unload", async (
            [FromBody] LlamaRuntimeUnloadRequest request,
            IConfiguration configuration,
            ILlamaServerRuntimeClient llamaClient,
            ILlamaRuntimeCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!SettingsGroupFactory.HasConfiguredLlamaRuntime(configuration))
            {
                return SettingsGroupFactory.LlamaRuntimeUnavailable();
            }

            var handle = coordinator.TryAcquireAliasLock(request.RouterModelId);
            if (handle == null)
            {
                var problem = new ProblemDetails
                {
                    Type = $"{RoutingProblemDetailsFactory.ProblemTypeBase}runtime-not-ready",
                    Title = "Local runtime busy",
                    Status = StatusCodes.Status409Conflict,
                    Detail = $"A load or unload operation is already in progress for alias '{request.RouterModelId}'."
                };
                problem.Extensions["code"] = RoutingErrorCodes.RuntimeNotReady;
                problem.Extensions["action"] =
                    "Wait for the in-flight operation on this alias to complete, then retry.";
                problem.Extensions["modelId"] = request.RouterModelId;
                return Results.Problem(problem);
            }

            await using var _ = handle;
            try
            {
                await llamaClient.UnloadModelAsync(request.RouterModelId, cancellationToken);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        })
        .WithName("UnloadLlamaRuntimeModel")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        llamaGroup.MapGet("/runtime/status", async (
            IConfiguration configuration,
            ILlamaRuntimeInventoryService inventoryService,
            ILlamaRuntimeCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!SettingsGroupFactory.HasConfiguredLlamaRuntime(configuration))
            {
                return SettingsGroupFactory.LlamaRuntimeUnavailable();
            }

            var inventory = await inventoryService.GetInventoryAsync(cancellationToken);
            var statuses = new List<LlamaRuntimeAliasStatusDto>(inventory.Count);
            foreach (var item in inventory)
            {
                var loaded = string.Equals(item.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase);
                var loading = string.Equals(item.RuntimeState, "loading", StringComparison.OrdinalIgnoreCase);
                var lockHeld = coordinator.IsAliasLocked(item.RouterModelId);

                statuses.Add(new LlamaRuntimeAliasStatusDto(
                    Alias: item.RouterModelId,
                    Loaded: loaded,
                    InProgress: loading || lockHeld,
                    RuntimeState: item.RuntimeState,
                    RouterModelId: item.RouterModelId,
                    LastLoadStartedAt: null,
                    LastLoadDurationMs: null,
                    LastError: null));
            }

            return Results.Ok((IReadOnlyList<LlamaRuntimeAliasStatusDto>)statuses);
        })
        .WithName("GetLlamaRuntimeStatus")
        .Produces<IReadOnlyList<LlamaRuntimeAliasStatusDto>>(StatusCodes.Status200OK);

        llamaGroup.MapPost("/downloads", async (
            HttpRequest httpRequest,
            [FromBody] StartModelDownloadRequest request,
            IHuggingFaceModelDownloadService downloadService,
            CancellationToken cancellationToken) =>
        {
            var internalAllowed = string.Equals(
                httpRequest.Headers["X-Guideants-Internal-Onboarding"].ToString(),
                "true",
                StringComparison.OrdinalIgnoreCase);
            if (!internalAllowed)
            {
                return Results.BadRequest(new
                {
                    error = "Direct onboarding downloads are internal-only. Use POST /api/settings/models:add."
                });
            }

            try
            {
                var op = await downloadService.StartDownloadAsync(request, cancellationToken);
                return Results.Accepted($"/api/settings/llama/downloads/{op.OperationId}", op);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("StartLlamaModelDownload")
        .Produces<ModelDownloadOperationDto>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest);

        llamaGroup.MapGet("/downloads/{operationId}", async (
            string operationId,
            ILocalModelOnboardingOrchestrator localModelOnboardingOrchestrator,
            CancellationToken cancellationToken) =>
        {
            var op = await localModelOnboardingOrchestrator.GetOperationStatusAsync(operationId, cancellationToken);
            return op == null ? Results.NotFound() : Results.Ok(op);
        })
        .WithName("GetLlamaModelDownloadStatus")
        .Produces<ModelDownloadOperationDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        llamaGroup.MapDelete("/router/entries/{routerModelId}", async (
            string routerModelId,
            ILlamaRuntimeInventoryService inventoryService,
            ILlamaServerRuntimeClient llamaClient,
            ILlamaRuntimeCoordinator coordinator,
            ILlamaRuntimeAdminClient adminClient,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            return await SettingsLlamaRouterDeleteHandler.DeleteLlamaRouterEntryAsync(
                routerModelId,
                inventoryService,
                llamaClient,
                coordinator,
                adminClient,
                settingsService,
                cancellationToken).ConfigureAwait(false);
        })
        .WithName("DeleteLlamaRouterEntry")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway);
    }
}
