using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsRoutingEndpoints
{
    public static void MapSettingsRoutingEndpoints(this WebApplication app)
    {
        var routingGroup = SettingsGroupFactory.MapRoutingGroup(app);

        routingGroup.MapGet("/chat-targets", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var targets = await settingsService.GetChatTargetsAsync(cancellationToken);
            return Results.Ok(targets);
        })
        .WithName("GetRoutingChatTargets")
        .Produces<IReadOnlyList<ChatTargetDto>>(StatusCodes.Status200OK);

        routingGroup.MapGet("/chat-targets/preflight", async (
            ApplicationDbContext db,
            IRoutingReadinessService readiness,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var distinctActive = await SettingsRoutingProbeSupport.GetDistinctActiveAssistantModelIdsAsync(db, cancellationToken);
            var anyAssistantsWithoutModel = await db.Assistants
                .AsNoTracking()
                .AnyAsync(a => a.IsActive && a.ModelId == null, cancellationToken);
            // Configuration surface (R-A): concurrent, NonBlocking. Feeds a readiness column;
            // nothing here loads/dispatches/runs, so it must never block on an engine (F-3).
            var results = await Task.WhenAll(distinctActive.Select(modelId =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = SettingsRoutingProbeSupport.InferChatTargetReferenceKind(modelId, configuration, anyAssistantsWithoutModel);
                return readiness.ProbeChatTargetAsync(modelId, cancellationToken, kind, RoutingProbeMode.NonBlocking);
            }));

            return Results.Ok((IReadOnlyList<ChatTargetReadinessDto>)results);
        })
        .WithName("GetRoutingChatTargetsPreflight")
        .Produces<IReadOnlyList<ChatTargetReadinessDto>>(StatusCodes.Status200OK);

        routingGroup.MapGet("/chat-targets/{modelId}/readiness", async (
            string modelId,
            [FromQuery] bool strict,
            IRoutingReadinessService readiness,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return Results.BadRequest(new { error = "modelId is required" });
            }

            // A-1 (resolved): `strict` is response-shaping only — this route probes identically
            // for both values and no action here loads/dispatches/runs, so it is configuration
            // under R-A → NonBlocking. The 404/409 problem+json shapes are preserved unchanged;
            // verdicts remain available at the real use paths (dispatch preflight, chat-readiness).
            var result = await readiness.ProbeChatTargetAsync(modelId, cancellationToken, "direct", RoutingProbeMode.NonBlocking);

            var modelMissing = result.Blockers.Any(b =>
                b.StartsWith(RoutingReadinessService.BlockerKeys.ModelMissing + ":", StringComparison.Ordinal));
            if (modelMissing)
            {
                var problem = new ProblemDetails
                {
                    Type = $"{RoutingProblemDetailsFactory.ProblemTypeBase}model-not-found",
                    Title = "Catalog model not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Catalog model '{modelId}' was not found."
                };
                problem.Extensions["code"] = RoutingReadinessService.BlockerKeys.ModelMissing;
                problem.Extensions["action"] =
                    "Add the model under Settings → Models & Runtime → Catalog, or update the assistant to reference an existing model id.";
                problem.Extensions["modelId"] = modelId;
                problem.Extensions["blockers"] = result.Blockers;
                return Results.Problem(problem);
            }

            if (strict && string.Equals(result.Status, "blocked", StringComparison.Ordinal))
            {
                var problem = new ProblemDetails
                {
                    Type = $"{RoutingProblemDetailsFactory.ProblemTypeBase}model-not-ready",
                    Title = "Routed model not ready",
                    Status = StatusCodes.Status409Conflict,
                    Detail = $"Model '{modelId}' is not ready: {result.Blockers.Count} blocker(s)."
                };
                problem.Extensions["code"] = RoutingErrorCodes.ModelNotReady;
                problem.Extensions["action"] =
                    "Resolve the listed blockers under Settings → Connections / Models & Runtime before routing chat traffic to this model.";
                problem.Extensions["modelId"] = modelId;
                problem.Extensions["blockers"] = result.Blockers;
                return Results.Problem(problem);
            }

            return Results.Ok(result);
        })
        .WithName("GetChatTargetReadiness")
        .Produces<ChatTargetReadinessDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
    }
}
