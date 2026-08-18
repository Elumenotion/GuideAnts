using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsServiceLocalModelsEndpoints
{
    public static void MapSettingsServiceLocalModelsEndpoints(this WebApplication app)
    {
        var serviceEditorsGroup = SettingsGroupFactory.MapServiceEditorsGroup(app);

        serviceEditorsGroup.MapGet("/{serviceId}/local-models", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IApplicationSettingsService settings,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? "/admin/bundles"
                : "/admin/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}{path}");
            if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
            {
                return await ServiceLocalModelListEnricher.ProxyAndEnrichImageBundlesAsync(
                    httpClientFactory.CreateClient(),
                    request,
                    settings,
                    cancellationToken).ConfigureAwait(false);
            }

            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModels");

        serviceEditorsGroup.MapGet("/{serviceId}/local-models/catalog", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(serviceId, "SpeechTranscription", StringComparison.Ordinal)
                && !string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal)
                && !string.Equals(serviceId, "Embeddings", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = $"Service '{serviceId}' does not expose a curated model catalog." });
            }

            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}/admin/catalog");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModelCatalog");

        // Baked voice-pack presets for TTS models whose catalog voiceInput is
        // voice_pack (e.g. chatterbox). Not a Hugging Face download — the pack
        // ships in the image. The client uses this to populate the voice picker
        // instead of any hardcoded enum.
        serviceEditorsGroup.MapGet("/{serviceId}/local-models/voice-pack", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = $"Service '{serviceId}' does not expose a voice pack." });
            }

            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}/admin/voice-pack");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModelVoicePack");

        // Runtime speaker ids / server preset names for the loaded TTS model
        // (audiocpp_server GET /v1/audio/voices). Used for catalog voiceInput
        // builtin entries in the settings UI.
        serviceEditorsGroup.MapGet("/{serviceId}/local-models/voices", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = $"Service '{serviceId}' does not expose runtime voices." });
            }

            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}/admin/voices");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModelVoices");

        // Readiness / runtime snapshot for local services that expose /ready
        // (ASR, TTS, Embeddings). Image Generation's SD wrapper exposes
        // /health but its "active bundle" state is observable via
        // /admin/bundles, so it stays on its own shape and is excluded here.
        serviceEditorsGroup.MapGet("/{serviceId}/runtime-readiness", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(serviceId, "SpeechTranscription", StringComparison.Ordinal)
                && !string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal)
                && !string.Equals(serviceId, "Embeddings", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = $"Service '{serviceId}' does not expose a runtime-readiness probe." });
            }
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}/ready");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceRuntimeReadiness");

        serviceEditorsGroup.MapPost("/{serviceId}/local-models/downloads", async (
            string serviceId,
            [FromBody] JsonElement payload,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IHuggingFaceTokenResolver hfTokenResolver,
            IApplicationSettingsService settings,
            IBundleDefinitionProjectionService projectionService,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            var validationError = ServiceLocalModelDownloadValidator.ValidateDownloadPayload(serviceId, payload);
            if (validationError is not null)
            {
                return validationError;
            }

            if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
            {
                var definition = SettingsImageGenerationBundleDefinitionsEndpoints.TryMapDownloadPayloadToDefinition(payload);
                if (definition is null)
                {
                    return Results.BadRequest(new { error = "ImageGeneration download payload could not be mapped to a canonical bundle definition." });
                }

                await settings.UpsertImageGenerationBundleDefinitionAsync(definition, cancellationToken);
                await projectionService.ProjectBundleAsync(definition.BundleId, cancellationToken);
            }

            if (ServiceLocalModelCatalogSupport.ExposesCuratedCatalog(serviceId)
                && LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var modelId))
            {
                var catalogResult = await ServiceLocalModelCatalogSupport.GetCatalogIdsAsync(
                    serviceId,
                    configuration,
                    httpClientFactory.CreateClient(),
                    cancellationToken);
                if (catalogResult.Error is not null)
                {
                    return catalogResult.Error;
                }

                var catalogMembershipError = ServiceLocalModelDownloadValidator.ValidateCatalogMembership(
                    modelId,
                    catalogResult.Ids!);
                if (catalogMembershipError is not null)
                {
                    return catalogMembershipError;
                }
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? "/admin/bundles/download"
                : "/admin/models/download";

            // Stamp the single, server-resolved Hugging Face token into the
            // forwarded body so the downstream sd/asr/tts admin service uses
            // the one configured value for every Hugging Face call. Any
            // `hf_token` the client tried to pass is overwritten on purpose.
            var resolvedHfToken = hfTokenResolver.Resolve();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}{path}")
            {
                Content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, resolvedHfToken),
            };
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("StartServiceLocalModelDownload");

        serviceEditorsGroup.MapGet("/{serviceId}/local-models/operations/{operationId}", async (
            string serviceId,
            string operationId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? $"/admin/bundles/operations/{Uri.EscapeDataString(operationId)}"
                : $"/admin/models/{Uri.EscapeDataString(operationId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModelOperation");

        serviceEditorsGroup.MapPost("/{serviceId}/local-models/operations/{operationId}/cancel", async (
            string serviceId,
            string operationId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? $"/admin/bundles/operations/{Uri.EscapeDataString(operationId)}/cancel"
                : $"/admin/models/{Uri.EscapeDataString(operationId)}/cancel";

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("CancelServiceLocalModelOperation");

        // Load / activate a model for an auxiliary local service (ASR, TTS, Embeddings,
        // Image Generation).
        //
        // GuideAntsApi is the loading-policy authority. This endpoint persists the
        // selection in ServiceModes; LocalAiStartupWarmupService derives a complete
        // plan from live routing and sends it to ga-admin for mechanical execution.
        // A load requested while the local provider is inactive is refused (409).
        //
        //  - ASR / TTS / Embeddings: optional model_path or model_id selects a specific
        //    downloaded model folder; the ref is persisted verbatim on ServiceModes
        //    and sent in the API-owned lifecycle plan as modelPath.
        //  - Image Generation: bundle_id (or model_path/model_id alias) is persisted on
        //    ServiceModes and sent in the API-owned lifecycle plan as bundleId.
        serviceEditorsGroup.MapPost("/{serviceId}/local-models/load", async (
            string serviceId,
            [FromBody] JsonElement payload,
            ILocalAiStartupWarmupService warmup,
            CancellationToken cancellationToken) =>
        {
            var isImageGeneration = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal);
            var isAsr = string.Equals(serviceId, "SpeechTranscription", StringComparison.Ordinal);
            var isTts = string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal);
            var isEmbeddings = string.Equals(serviceId, "Embeddings", StringComparison.Ordinal);
            if (!isImageGeneration && !isAsr && !isTts && !isEmbeddings)
            {
                return Results.BadRequest(new { error = $"Service '{serviceId}' does not expose a local model load endpoint." });
            }

            string? requestedModelRef = null;
            if (isImageGeneration)
            {
                if (LocalServiceAdminRouting.TryGetNonEmptyString(payload, "bundle_id", out var bundleId))
                {
                    requestedModelRef = bundleId;
                }
                else if (LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_path", out var modelPath))
                {
                    requestedModelRef = modelPath;
                }
                else if (LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var modelId))
                {
                    requestedModelRef = modelId;
                }
            }
            else if (LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_path", out var modelPath))
            {
                requestedModelRef = modelPath;
            }
            else if (LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var modelId))
            {
                requestedModelRef = modelId;
            }

            var result = await warmup
                .ReconcileLocalServiceAsync(serviceId, requestedModelRef, cancellationToken)
                .ConfigureAwait(false);
            return MapReconcileResult(serviceId, result);
        })
        .WithName("LoadServiceLocalModel");

        serviceEditorsGroup.MapPost("/{serviceId}/local-models/unload", async (
            string serviceId,
            ILocalAiStartupWarmupService warmup,
            CancellationToken cancellationToken) =>
        {
            var isImageGeneration = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal);
            var isAsr = string.Equals(serviceId, "SpeechTranscription", StringComparison.Ordinal);
            var isTts = string.Equals(serviceId, "SpeechSynthesis", StringComparison.Ordinal);
            var isEmbeddings = string.Equals(serviceId, "Embeddings", StringComparison.Ordinal);
            if (!isImageGeneration && !isAsr && !isTts && !isEmbeddings)
            {
                return Results.BadRequest(new { error = $"Service '{serviceId}' does not expose a local model unload endpoint." });
            }

            var result = await warmup
                .PowerOffLocalServiceEngineAsync(serviceId, cancellationToken)
                .ConfigureAwait(false);
            return MapReconcileResult(serviceId, result);
        })
        .WithName("UnloadServiceLocalModel");

        // Select a downloaded model/bundle in API-owned ServiceModes, then submit the
        // routing-derived plan. ga-admin executes a load only when the local provider
        // is active; otherwise the API refuses the request (409).
        serviceEditorsGroup.MapPost("/{serviceId}/local-models/{modelRef}/select-active", async (
            string serviceId,
            string modelRef,
            ILocalAiStartupWarmupService warmup,
            CancellationToken cancellationToken) =>
        {
            var result = await warmup
                .ReconcileLocalServiceAsync(serviceId, modelRef, cancellationToken)
                .ConfigureAwait(false);
            return MapReconcileResult(serviceId, result);
        })
        .WithName("SelectServiceLocalModel");

        serviceEditorsGroup.MapGet("/{serviceId}/local-models/{modelRef}", async (
            string serviceId,
            string modelRef,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? $"/admin/bundles/{Uri.EscapeDataString(modelRef)}"
                : $"/admin/models/{Uri.EscapeDataString(modelRef)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModel");

        serviceEditorsGroup.MapDelete("/{serviceId}/local-models/{modelRef}", async (
            string serviceId,
            string modelRef,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? $"/admin/bundles/{Uri.EscapeDataString(modelRef)}"
                : $"/admin/models/{Uri.EscapeDataString(modelRef)}";
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("DeleteServiceLocalModel");
    }

    private static IResult MapReconcileResult(string serviceId, LocalServiceReconcileResult result)
    {
        return result.Outcome switch
        {
            LocalServiceReconcileOutcome.Warm => Results.Ok(new { serviceId, status = "loaded" }),
            LocalServiceReconcileOutcome.Idle => Results.Ok(new { serviceId, status = "unloaded" }),
            LocalServiceReconcileOutcome.NotActiveProvider => Results.Conflict(new
            {
                error = result.Detail ?? $"'{serviceId}' is not the active provider; nothing was loaded.",
            }),
            LocalServiceReconcileOutcome.Unavailable => SettingsGroupFactory.LocalServiceUnavailable(serviceId),
            LocalServiceReconcileOutcome.RoutingUnknown => Results.Conflict(new
            {
                error = result.Detail ?? $"Routing for '{serviceId}' could not be resolved.",
            }),
            LocalServiceReconcileOutcome.Timeout => Results.Json(
                new { error = result.Detail ?? $"'{serviceId}' did not reach the desired state in time." },
                statusCode: StatusCodes.Status504GatewayTimeout),
            _ => Results.Json(
                new { error = result.Detail ?? $"Reconcile for '{serviceId}' failed." },
                statusCode: StatusCodes.Status502BadGateway),
        };
    }
}
