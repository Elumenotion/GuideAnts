using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Endpoints;

public static class NotebookLlamaRuntimeEndpoints
{
    public static void MapNotebookLlamaRuntimeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/notebooks/{notebookId:guid}/llama-runtime")
            .WithTags("NotebookLlamaRuntime");

        group.MapGet("/", async (
            [FromServices] INotebookModelRuntimeService runtimeService,
            Guid notebookId,
            [FromQuery] Guid? assistantId,
            CancellationToken cancellationToken) =>
        {
            var status = await runtimeService.GetRuntimeStatusAsync(notebookId, assistantId, cancellationToken);
            return Results.Ok(status);
        })
        .Produces<NotebookLlamaRuntimeStatusDto>(StatusCodes.Status200OK);

        group.MapPost("/load", async (
            [FromServices] INotebookModelRuntimeService runtimeService,
            Guid notebookId,
            [FromBody] LoadRuntimeRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var op = await runtimeService.StartLoadOperationAsync(notebookId, request.AssistantId, cancellationToken);
                return Results.Accepted($"/api/notebooks/{notebookId}/llama-runtime/operations/{op.OperationId}", op);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .Produces<ModelLoadOperationDto>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/unload", async (
            [FromServices] INotebookModelRuntimeService runtimeService,
            Guid notebookId,
            [FromBody] UnloadRuntimeRequest? request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var op = await runtimeService.StartUnloadForNotebookContextAsync(
                    notebookId,
                    request?.AssistantId,
                    cancellationToken);
                return Results.Accepted(
                    $"/api/notebooks/{notebookId}/llama-runtime/operations/{op.OperationId}",
                    op);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .Produces<ModelLoadOperationDto>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/operations/{operationId}", async (
            [FromServices] INotebookModelRuntimeService runtimeService,
            Guid notebookId,
            string operationId,
            CancellationToken cancellationToken) =>
        {
            var op = await runtimeService.GetOperationStatusAsync(notebookId, operationId, cancellationToken);
            if (op == null)
            {
                return Results.NotFound();
            }
            return Results.Ok(op);
        })
        .Produces<ModelLoadOperationDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Operator-initiated llama-server restart. Fronted as a per-notebook endpoint so the
        // browser UI's existing notebook-scoped auth + routing apply; the underlying work is
        // container-wide (one llama-server per ai container).
        group.MapPost("/restart", async (
            [FromServices] ILlamaRuntimeAdminClient adminClient,
            [FromServices] ILogger<LlamaRuntimeRestartLogger> logger,
            Guid notebookId,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await adminClient.RestartLlamaServerAsync(cancellationToken);
                logger.LogInformation(
                    "Llama-server restart requested for notebook {NotebookId}. termed={Termed} oldPid={OldPid} newPid={NewPid}",
                    notebookId,
                    result.Termed,
                    result.OldPid,
                    result.NewPid);
                return Results.Ok(result);
            }
            catch (TimeoutException ex)
            {
                logger.LogWarning(ex, "Llama-server restart timed out for notebook {NotebookId}", notebookId);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status504GatewayTimeout,
                    title: "Local model runtime did not come back online in time.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Llama-server restart failed for notebook {NotebookId}", notebookId);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Failed to restart local model runtime.");
            }
        })
        .Produces<LlamaAdminRestartResultDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status502BadGateway)
        .Produces(StatusCodes.Status504GatewayTimeout);

        // Admin read-only snapshot
        app.MapGet("/api/admin/llama/runtime", async (
            [FromServices] ILlamaServerRuntimeClient llamaClient,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var models = await llamaClient.ListModelsAsync(cancellationToken);
                return Results.Ok(models);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        })
        .WithTags("Admin")
        .Produces<LlamaModelsResponse>(StatusCodes.Status200OK);
    }

    public record LoadRuntimeRequest(Guid? AssistantId);

    public record UnloadRuntimeRequest(Guid? AssistantId);

    // Marker type: gives the restart endpoint a stable, descriptive logger category without
    // taking a dependency on a service-layer class.
    internal sealed class LlamaRuntimeRestartLogger { }
}