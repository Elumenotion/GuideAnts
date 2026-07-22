using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Bootstrap;
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

        llamaGroup.MapGet("/catalog", async (
            ILlamaRuntimeAdminClient adminClient,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var catalog = await adminClient.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
                return Results.Ok(catalog);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Llama catalog unavailable",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["code"] = "LLAMA_CATALOG_UNAVAILABLE" });
            }
        })
        .WithName("GetLlamaCatalog")
        .Produces<LlamaCatalogResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        llamaGroup.MapGet("/catalog/{catalogId}/quants", async (
            string catalogId,
            string? catalogVersion,
            ILlamaRuntimeAdminClient adminClient,
            GuideAntsApi.Services.HuggingFace.IHuggingFaceTokenResolver tokenResolver,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var quants = await adminClient.GetCatalogQuantsAsync(
                    catalogId,
                    catalogVersion,
                    tokenResolver.Resolve(),
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(quants);
            }
            catch (LlamaCatalogServiceException ex)
            {
                return Results.Problem(
                    title: "Llama catalog quants request failed",
                    detail: ex.Message,
                    statusCode: ex.StatusCode,
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Llama catalog quants unavailable",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["code"] = "LLAMA_CATALOG_UNAVAILABLE" });
            }
        })
        .WithName("GetLlamaCatalogQuants")
        .Produces<LlamaCatalogQuantsResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status502BadGateway);

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
            ILlamaRuntimeCoordinator coordinator,
            ILocalAiWarmupService localAiWarmup,
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
                await localAiWarmup.SyncDesiredAndApplyAsync(
                    new WarmupDesiredBuildOptions
                    {
                        LlamaRouterAliasOverride = request.RouterModelId.Trim(),
                    },
                    waitForCompletion: true,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok();
            }
            catch (Exception ex)
            {
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
                    LastError: item.RuntimeFailed
                        ? item.RuntimeExitCode is int exitCode
                            ? $"llama-server child exited with status {exitCode}."
                            : "llama-server child exited during model load."
                        : null));
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

        llamaGroup.MapGet("/operations/{operationId}", async (
            string operationId,
            ILocalModelOnboardingOrchestrator localModelOnboardingOrchestrator,
            CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(operationId, out var operationGuid))
            {
                return Results.NotFound();
            }

            try
            {
                var op = await localModelOnboardingOrchestrator
                    .GetCuratedOperationStatusAsync(operationGuid, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(op);
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound();
            }
        })
        .WithName("GetLlamaCuratedOperationStatus")
        .Produces<LlamaOperationStatusDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        llamaGroup.MapGet("/router/entries", async (
            ILlamaRuntimeAdminClient adminClient,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var entries = await adminClient.GetRouterEntriesAsync(cancellationToken).ConfigureAwait(false);
                var mapped = entries.Entries
                    .Select(e => new LlamaRouterEntryDto(
                        Alias: e.Alias,
                        ModelPath: e.ModelPath ?? string.Empty,
                        MmprojPath: e.MmprojPath ?? string.Empty,
                        HasModelFile: e.HasModelFile,
                        HasMmprojFile: e.HasMmprojFile,
                        ContextSize: e.ContextSize,
                        CacheRamMib: e.CacheRamMib,
                        Preset: e.Preset ?? new Dictionary<string, string>()))
                    .ToList();
                return Results.Ok(new LlamaRouterEntriesResponseDto(mapped));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Llama router entries unavailable",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["code"] = "LLAMA_ROUTER_UNAVAILABLE" });
            }
        })
        .WithName("GetLlamaRouterEntries")
        .Produces<LlamaRouterEntriesResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        llamaGroup.MapPut("/router/entries/{alias}", async (
            string alias,
            [FromBody] LlamaRouterEntryPutRequest request,
            ILlamaRuntimeAdminClient adminClient,
            ApplicationDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(alias.Trim(), request.Alias.Trim(), StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Route alias must match request alias." });
            }

            try
            {
                // Catalog editor Save is WYSIWYG: omitted preset keys must be deleted.
                // Older clients still send presetMode=merge, which preserves removals.
                var replaceRequest = request with { PresetMode = "replace" };
                var result = await adminClient.PutRouterEntryAsync(replaceRequest, cancellationToken).ConfigureAwait(false);
                await UpdateRouterPresetSnapshotAsync(db, replaceRequest, cancellationToken).ConfigureAwait(false);
                if (result.RuntimeApply is { Applied: false })
                {
                    return Results.Json(
                        new
                        {
                            ok = result.Ok,
                            iniSha256 = result.IniSha256,
                            runtimeApply = new
                            {
                                applied = result.RuntimeApply.Applied,
                                iniSha256 = result.RuntimeApply.IniSha256,
                                remediation = result.RuntimeApply.Remediation,
                            },
                        },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Ok(new
                {
                    ok = result.Ok,
                    iniSha256 = result.IniSha256,
                    runtimeApply = result.RuntimeApply is null
                        ? null
                        : new
                        {
                            applied = result.RuntimeApply.Applied,
                            iniSha256 = result.RuntimeApply.IniSha256,
                            remediation = result.RuntimeApply.Remediation,
                        },
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Llama router entry update failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["code"] = "LLAMA_ROUTER_UPDATE_FAILED" });
            }
        })
        .WithName("PutLlamaRouterEntry")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status502BadGateway);

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

        llamaGroup.MapSettingsLlamaInstallationEndpoints();
    }

    private static async Task UpdateRouterPresetSnapshotAsync(
        ApplicationDbContext db,
        LlamaRouterEntryPutRequest request,
        CancellationToken cancellationToken)
    {
        var preset = new Dictionary<string, string>(request.Preset ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (request.ContextSize is int contextSize)
        {
            preset["ctx-size"] = contextSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (request.CacheRamMib is int cacheRamMib)
        {
            preset["cache-ram"] = cacheRamMib.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            preset.Remove("cache-ram");
        }

        var now = DateTime.UtcNow;
        var installations = await db.LocalModelInstallations
            .Where(installation => installation.RouterModelId == request.Alias)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var installation in installations)
        {
            installation.RouterPresetSnapshotJson = JsonSerializer.Serialize(preset);
            installation.UpdatedUtc = now;
        }

        if (installations.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
