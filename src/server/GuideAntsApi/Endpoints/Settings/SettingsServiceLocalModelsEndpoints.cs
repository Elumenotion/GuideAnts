using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.Reachability;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Settings;

namespace GuideAntsApi.Endpoints.Settings;

public static class SettingsServiceLocalModelsEndpoints
{
    public static void MapSettingsServiceLocalModelsEndpoints(this WebApplication app)
    {
        var serviceEditorsGroup = SettingsGroupFactory.MapServiceEditorsGroup(app);

        // P5b (grounded in R5's "configuration stays fully usable"): the editor page calls
        // this on mount. A raw 503 when the engine is down is our code, not physics. When the
        // endpoint is unreachable we serve the last-known inventory from the Plane C mirror
        // (marked last-known per S-2), so the list renders with a timestamp instead of 503ing
        // the whole editor. When the mirror has never observed anything, a named state is
        // returned — never a blank, never a spinner (F-2).
        serviceEditorsGroup.MapGet("/{serviceId}/local-models", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IApplicationSettingsService settings,
            ILocalInventoryMirror inventoryMirror,
            ILocalEndpointCache endpointCache,
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
            var listUrl = $"{adminBase}{path}";

            if (!endpointCache.TryGet($"{adminBase.TrimEnd('/')}{(string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal) ? "/health" : "/ready")}", out var observation)
                || observation.Availability == EndpointAvailability.Unreachable)
            {
                // Unreachable (or never observed): serve last-known from the mirror.
                if (inventoryMirror.TryGet(serviceId, out var models, out var source, out var observedUtc)
                    && source != InventorySource.None)
                {
                    var items = models.Select(m => new Dictionary<string, object?>
                    {
                        ["modelRef"] = m.ModelRef,
                        ["complete"] = m.IsComplete,
                        ["active"] = m.IsActive,
                        ["lastKnown"] = true,
                        ["observedUtc"] = observedUtc,
                    }).ToList();
                    return Results.Ok(new
                    {
                        source = source.ToString(),
                        observedUtc,
                        items,
                    });
                }

                return Results.Json(
                    new
                    {
                        error = $"The local {serviceId} endpoint is not reachable; its model list has not been observed yet.",
                        code = "ENDPOINT_UNREACHABLE",
                        serviceId,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, listUrl);
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
        serviceEditorsGroup.MapGet("/{serviceId}/runtime-readiness", (
            string serviceId,
            IConfiguration configuration,
            ILocalEndpointCache endpointCache) =>
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

            // P5b: a raw inline /ready proxy with no budget is a second 503 on the editor
            // mount path. What this displays is governed by F-2: named state from the Plane C
            // cache (with observed timestamp); never a raw socket wait. The handler is therefore
            // synchronous (no await, no proxy) - same shape as GET /reachability.
            var readinessUrl = $"{adminBase.TrimEnd('/')}/ready";
            if (!endpointCache.TryGet(readinessUrl, out var observation))
            {
                return Results.Ok(new
                {
                    serviceId,
                    state = (string?)"unknown",
                    availability = "Unknown",
                    observedUtc = (DateTime?)null,
                    note = "not yet observed · Refresh",
                });
            }

            return Results.Ok(new
            {
                serviceId,
                state = observation.RuntimeState,
                availability = observation.Availability.ToString(),
                observedUtc = observation.ObservedUtc,
                error = observation.Error,
            });
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
            IServiceLocalModelInstallationStore installStore,
            ILocalEndpointCache endpointCache,
            CancellationToken cancellationToken) =>
        {
            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return SettingsGroupFactory.LocalServiceUnavailable(serviceId);
            }

            // P5b/R-B: a download needs a live engine — while unreachable it stays out of
            // scope, but the affordance must fail with a named reason, not a raw 503.
            var downloadReadinessUrl = $"{adminBase.TrimEnd('/')}{(string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal) ? "/health" : "/ready")}";
            if (!endpointCache.TryGet(downloadReadinessUrl, out var downloadObservation)
                || downloadObservation.Availability == EndpointAvailability.Unreachable)
            {
                return Results.Json(
                    new
                    {
                        error = $"The local {serviceId} endpoint is not reachable; downloading requires a live engine.",
                        code = "ENDPOINT_UNREACHABLE",
                        serviceId,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
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
            var upstream = await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);

            // Plane B (F-5): record install memory at acceptance, from what the API already
            // possesses — its own payload coordinates plus the engine's catalog version read
            // once at acceptance. A download requires a live engine by definition, so this
            // read stays inside the non-goals; no other engine interrogation happens.
            try
            {
                if (TryExtractDownloadAccepted(upstream, payload, out var modelRef, out var operationId))
                {
                    string? catalogVersion = null;
                    try
                    {
                        catalogVersion = await ServiceLocalModelCatalogSupport.TryReadCatalogVersionAsync(
                            serviceId, configuration, httpClientFactory.CreateClient(), cancellationToken);
                    }
                    catch
                    {
                        // Version is enrichment, not truth: absence is recorded as null.
                    }

                    var catalogId = LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var mid) ? mid
                        : LocalServiceAdminRouting.TryGetNonEmptyString(payload, "bundle_id", out var bid) ? bid
                        : null;
                    var requestedRevision = LocalServiceAdminRouting.TryGetNonEmptyString(payload, "revision", out var rev) ? rev : null;
                    var isBundle = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal);

                    await installStore.UpsertAsync(
                        serviceId,
                        modelRef,
                        isBundle ? "bundle" : "model",
                        catalogId,
                        catalogVersion,
                        requestedRevision,
                        "Accepted",
                        operationId,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Install memory is best-effort at acceptance: a recording failure must not
                // fail a download the engine already accepted. Absence of a row renders as
                // "provenance unknown — verify or remove", never a null reference.
                Console.Error.WriteLine($"install-memory recording failed for {serviceId}: {ex.Message}");
            }

            return upstream;
        })
        .WithName("StartServiceLocalModelDownload");

        serviceEditorsGroup.MapGet("/{serviceId}/local-models/operations/{operationId}", async (
            string serviceId,
            string operationId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IServiceLocalModelInstallationStore installStore,
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
            var upstream = await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);

            // Plane B: on a terminal engine verdict, record it. Terminal success enriches the
            // row with target directory + artifact path/size from the admin listing (the only
            // place the engines report them); terminal failure stamps Failed + ObservedError.
            // Engine contract unchanged (O-2): we read the same operation dict the UI polls.
            try
            {
                var json = ExtractJsonBody(upstream);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString()
                        : null;
                    var row = await installStore.FindByOperationAsync(serviceId, operationId, cancellationToken).ConfigureAwait(false);
                    if (row is not null)
                    {
                        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                        {
                            var finalRef = root.TryGetProperty("modelRef", out var mr) && mr.ValueKind == JsonValueKind.String
                                ? mr.GetString()
                                : row.ModelRef;
                            var (targetDir, artifacts) = await TryReadArtifactsAsync(
                                serviceId, adminBase, httpClientFactory.CreateClient(), finalRef ?? row.ModelRef, cancellationToken).ConfigureAwait(false);
                            await installStore.SetInstalledAsync(serviceId, row.ModelRef, targetDir, artifacts, cancellationToken).ConfigureAwait(false);
                        }
                        else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
                        {
                            var error = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String
                                ? err.GetString()
                                : status;
                            await installStore.MarkFailedAsync(serviceId, row.ModelRef, error, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Enrichment is best-effort; the poll response is the engine's verbatim body
                // regardless (the row converges on a later poll).
            }

            return upstream;
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

        // F-1 — delete is a configuration act, not a use path: it must succeed with the
        // engine unreachable (R5/R-B). The DB row is removed and a dangling ServiceModes
        // selection is cleared; the engine DELETE is a best-effort courtesy whose outcome
        // never changes the result (diskCleanup true|false). Nothing is queued, nothing is
        // replayed, no deferred cleanup exists (T-A4).
        serviceEditorsGroup.MapDelete("/{serviceId}/local-models/{modelRef}", async (
            string serviceId,
            string modelRef,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IServiceLocalModelInstallationStore installStore,
            IApplicationSettingsService settings,
            CancellationToken cancellationToken) =>
        {
            var diskCleanup = false;

            var adminBase = LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);
            if (!string.IsNullOrWhiteSpace(adminBase))
            {
                var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                    ? $"/admin/bundles/{Uri.EscapeDataString(modelRef)}"
                    : $"/admin/models/{Uri.EscapeDataString(modelRef)}";
                using var request = new HttpRequestMessage(HttpMethod.Delete, $"{adminBase}{path}");
                var upstream = await LocalServiceAdminRouting.ProxyAsync(
                    httpClientFactory.CreateClient(), request, cancellationToken);
                // A pass-through success (2xx incl. 204) counts as disk cleanup; anything the
                // proxy converted to an envelope (502) or a non-pass-through status does not.
                // The proxy returns ContentHttpResult for pass-throughs with the upstream
                // status in its StatusCode; envelopes are JsonHttpResult (502).
                diskCleanup = TryGetUpstreamStatusCode(upstream) is >= 200 and < 300;
            }

            await installStore.RemoveAsync(serviceId, modelRef, cancellationToken).ConfigureAwait(false);

            // Clear a dangling selection: the removed ref must not keep pointing at a
            // deleted artifact. Compare then clear via the API-owned settings surface.
            var current = await LocalServiceModeSelectionReader.TryReadLocalModelRefAsync(settings, serviceId, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current, modelRef, StringComparison.Ordinal))
            {
                await settings.ClearServiceModeModelIdAsync(serviceId, cancellationToken).ConfigureAwait(false);
            }

            return Results.Ok(new
            {
                serviceId,
                modelRef,
                deleted = true,
                diskCleanup,
            });
        })
        .WithName("DeleteServiceLocalModel");
    }

    /// <summary>
    /// Extracts (modelRef, operationId) from an accepted download response. The engines
    /// return 202 with the operation dict — {"operationId": "...", "modelRef"/"modelId"/
    /// "model_id": ...}. The ref falls back to the API's own payload when the engine does
    /// not echo one (SD returns the bundle id it was given). Best-effort: false when the
    /// upstream returned an error envelope or an unparseable body.
    /// </summary>
    private static bool TryExtractDownloadAccepted(IResult upstream, JsonElement payload, out string modelRef, out string? operationId)
    {
        modelRef = string.Empty;
        operationId = null;
        var json = ExtractJsonBody(upstream);
        if (string.IsNullOrWhiteSpace(json))
        {
            // Fall back to the API's own payload coordinates: the row is still created
            // (Accepted) when we cannot parse the engine's operation dict.
            modelRef = LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_path", out var mp) ? mp
                : LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var mi) ? mi
                : LocalServiceAdminRouting.TryGetNonEmptyString(payload, "bundle_id", out var bi) ? bi
                : string.Empty;
            return !string.IsNullOrWhiteSpace(modelRef);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? GetStr(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            operationId = GetStr("operationId") ?? GetStr("operation_id");
            modelRef = GetStr("modelRef") ?? GetStr("model_id") ?? GetStr("modelId") ?? GetStr("bundleId") ?? GetStr("bundle_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelRef))
            {
                modelRef = LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_path", out var mp) ? mp
                    : LocalServiceAdminRouting.TryGetNonEmptyString(payload, "model_id", out var mi) ? mi
                    : LocalServiceAdminRouting.TryGetNonEmptyString(payload, "bundle_id", out var bi) ? bi
                    : string.Empty;
            }
            return !string.IsNullOrWhiteSpace(modelRef);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the /admin/models (or /admin/bundles) listing for one ref and extracts its
    /// install path and size bytes — the fields the listing exposes (asr_service.py:988)
    /// and the operation dicts do not. Returns (null, "[]") on any failure.
    /// </summary>
    private static async Task<(string? TargetDirectory, string ArtifactsJson)> TryReadArtifactsAsync(
        string serviceId, string adminBase, HttpClient client, string modelRef, CancellationToken cancellationToken)
    {
        try
        {
            var listPath = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? "/admin/bundles"
                : "/admin/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}{listPath}");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, "[]");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("items", out var nested) ? nested : default;

            if (items.ValueKind != JsonValueKind.Array)
            {
                return (null, "[]");
            }

            foreach (var item in items.EnumerateArray())
            {
                var ref_ = item.TryGetProperty("modelRef", out var mr) && mr.ValueKind == JsonValueKind.String ? mr.GetString()
                    : item.TryGetProperty("bundleId", out var bi) && bi.ValueKind == JsonValueKind.String ? bi.GetString()
                    : item.TryGetProperty("model_id", out var mi) && mi.ValueKind == JsonValueKind.String ? mi.GetString()
                    : item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()
                    : null;
                if (!string.Equals(ref_, modelRef, StringComparison.Ordinal))
                {
                    continue;
                }

                var path = item.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
                var size = item.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : (long?)null;

                var artifact = new System.Text.Json.Nodes.JsonObject
                {
                    ["path"] = path,
                    ["sizeBytes"] = size,
                };
                var array = new System.Text.Json.Nodes.JsonArray { artifact };
                return (path, array.ToJsonString());
            }

            return (null, "[]");
        }
        catch (Exception)
        {
            return (null, "[]");
        }
    }

    /// <summary>
    /// Pulls the JSON body out of a proxy IResult. Pass-through results carry the body as
    /// content; envelopes (502) serialize their record shape — we only care about the
    /// pass-through case, so anything else returns null.
    /// </summary>
    /// <summary>
    /// Upstream HTTP status of a proxy IResult (ContentHttpResult.StatusCode for
    /// pass-throughs). Null when the result shape carries no status.
    /// </summary>
    private static int? TryGetUpstreamStatusCode(IResult upstream)
    {
        var property = upstream.GetType().GetProperty("StatusCode");
        if (property is null)
        {
            return null;
        }

        var value = property.GetValue(upstream);
        return value is int i ? i : null;
    }

    private static string? ExtractJsonBody(IResult upstream)
    {
        // ContentHttpResult and StatusCodeHttpResult are the shapes ProxyAsync returns;
        // reflection keeps this independent of the concrete closed generic types.
        var contentProperty = upstream.GetType().GetProperty("Content");
        var content = contentProperty?.GetValue(upstream) as string;
        return content;
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
