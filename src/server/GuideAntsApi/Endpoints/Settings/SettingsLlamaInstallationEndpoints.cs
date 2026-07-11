using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;

namespace GuideAntsApi.Endpoints.Settings;

internal static class SettingsLlamaInstallationEndpoints
{
    public static void MapSettingsLlamaInstallationEndpoints(this RouteGroupBuilder llamaGroup)
    {
        llamaGroup.MapGet("/installations/{modelId}", async (
            string modelId,
            ILocalModelLifecycleService lifecycleService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await lifecycleService
                    .GetInstallationDetailAsync(modelId, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(detail);
            }
            catch (LocalModelLifecycleException ex)
            {
                return Results.Problem(
                    title: "Installation detail unavailable",
                    detail: ex.Message,
                    statusCode: ex.StatusCode,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = ex.Code,
                        ["remediation"] = ex.Remediation,
                    });
            }
        })
        .WithName("GetLlamaInstallationDetail")
        .Produces<LlamaInstallationDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        llamaGroup.MapPost("/installations/{modelId}/change-quant", async (
            string modelId,
            [FromBody] ChangeQuantRequestDto request,
            ILocalModelLifecycleService lifecycleService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await lifecycleService
                    .StartChangeQuantAsync(modelId, request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Accepted($"/api/settings/llama/operations/{response.OperationId}", response);
            }
            catch (LocalModelLifecycleException ex)
            {
                return Results.Problem(
                    title: "Change quant failed",
                    detail: ex.Message,
                    statusCode: ex.StatusCode,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = ex.Code,
                        ["remediation"] = ex.Remediation,
                    });
            }
        })
        .WithName("PostLlamaInstallationChangeQuant")
        .Produces<LifecycleOperationResponseDto>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status409Conflict);

        llamaGroup.MapPost("/installations/{modelId}/repair", async (
            string modelId,
            [FromBody] RepairInstallationRequestDto request,
            ILocalModelLifecycleService lifecycleService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await lifecycleService
                    .StartRepairAsync(modelId, request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Accepted($"/api/settings/llama/operations/{response.OperationId}", response);
            }
            catch (LocalModelLifecycleException ex)
            {
                return Results.Problem(
                    title: "Repair failed",
                    detail: ex.Message,
                    statusCode: ex.StatusCode,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = ex.Code,
                        ["remediation"] = ex.Remediation,
                    });
            }
        })
        .WithName("PostLlamaInstallationRepair")
        .Produces<LifecycleOperationResponseDto>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status409Conflict);

        llamaGroup.MapPost("/installations/{modelId}/adopt", async (
            string modelId,
            [FromBody] AdoptInstallationRequestDto request,
            ILocalModelLifecycleService lifecycleService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!request.Confirm)
                {
                    var preview = await lifecycleService
                        .PreviewAdoptAsync(modelId, request.CatalogId, request.CatalogVersion, cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(preview);
                }

                var detail = await lifecycleService
                    .AdoptAsync(modelId, request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(detail);
            }
            catch (LocalModelLifecycleException ex)
            {
                return Results.Problem(
                    title: "Adoption failed",
                    detail: ex.Message,
                    statusCode: ex.StatusCode,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = ex.Code,
                        ["remediation"] = ex.Remediation,
                    });
            }
        })
        .WithName("PostLlamaInstallationAdopt")
        .Produces<LlamaInstallationDetailDto>(StatusCodes.Status200OK)
        .Produces<AdoptPreviewResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
