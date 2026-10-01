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
        // selection in ServiceModes and loads it directly on the service's own admin
        // endpoint. There is no multi-service plan and no cross-service coupling.
        //
        //  - ASR / TTS / Embeddings: optional model_path or model_id selects a specific
        //    downloaded model folder; the ref is persisted verbatim on ServiceModes
        //    and sent in the API-owned lifecycle plan as modelPath.
        //  - Image Generation: bundle_id (or model_path/model_id alias) is persisted on
        //    ServiceModes and sent in the API-owned lifecycle plan as bundleId.
        serviceEditorsGroup.MapPost("/{serviceId}/local-models/load", async (
            string serviceId,
            [FromBody] JsonElement payload,
            IApplicationSettingsService settings,
            ILocalServiceLoadService loadService,
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

            // Persist the selection in ServiceModes when a specific ref was requested
            // (a model selected in config is the API-owned selection authority).
            if (!string.IsNullOrWhiteSpace(requestedModelRef))
            {
                if (!LocalServiceModelRefRules.IsLoadableLocalModelRef(requestedModelRef))
                {
                    var refKind = isImageGeneration ? "bundle id" : "local model path";
                    return Results.BadRequest(new { error = $@"Model reference '{requestedModelRef}' is not a valid {refKind}." });
                }

                await settings
                    .SetServiceModeModelIdAsync(serviceId, requestedModelRef, cancellationToken)
                    .ConfigureAwait(false);
            }

            var effectiveRef = !string.IsNullOrWhiteSpace(requestedModelRef)
                ? requestedModelRef
                : await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(settings, serviceId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(effectiveRef))
            {
                var refKind = isImageGeneration ? "bundle" : "model";
                return Results.Conflict(new { error = $@"No local {refKind} is selected in ServiceModes. Select an active local {refKind} before loading." });
            }

            var result = await loadService
                .LoadServiceAsync(serviceId, effectiveRef, cancellationToken)
                .ConfigureAwait(false);
            return MapLoadResult(serviceId, result);
        })
        .WithName("LoadServiceLocalModel");

        serviceEditorsGroup.MapPost("/{serviceId}/local-models/unload", async (
            string serviceId,
            ILocalServiceLoadService loadService,
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

            var result = await loadService
                .UnloadServiceAsync(serviceId, cancellationToken)
                .ConfigureAwait(false);
            return MapLoadResult(serviceId, result);
        })
        .WithName("UnloadServiceLocalModel");

        // Select a downloaded model/bundle in API-owned ServiceModes, then submit the
        // routing-derived plan. ga-admin executes a load only when the local provider
        // is active; otherwise the API refuses the request (409).
        serviceEditorsGroup.MapPost("/{serviceId}/local-models/{modelRef}/select-active", async (
            string serviceId,
            string modelRef,
            IApplicationSettingsService settings,
            ILocalServiceLoadService loadService,
            CancellationToken cancellationToken) =>
        {
            await settings
                .SetServiceModeModelIdAsync(serviceId, modelRef, cancellationToken)
                .ConfigureAwait(false);
            var result = await loadService
                .LoadServiceAsync(serviceId, modelRef, cancellationToken)
                .ConfigureAwait(false);
            return MapLoadResult(serviceId, result);
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

    private static IResult MapLoadResult(string serviceId, LocalServiceOperationResult result)
    {
        if (result.Success)
        {
            var loaded = result.Readiness?.Loaded ?? false;
            return Results.Ok(new { serviceId, status = loaded ? "loaded" : "unloaded" });
        }

        return Results.Json(
            new { serviceId, error = result.Error ?? $"Operation for '{serviceId}' failed." },
            statusCode: StatusCodes.Status502BadGateway);
    }
}
