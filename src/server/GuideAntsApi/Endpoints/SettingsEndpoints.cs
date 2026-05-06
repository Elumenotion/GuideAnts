using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Settings;
using GuideAntsApi.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuideAntsApi.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings")
            .WithTags("Settings")
            .WithOpenApi();

        group.MapGet("/sections", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var sections = await settingsService.GetSectionSummariesAsync(cancellationToken);
            return Results.Ok(sections);
        })
        .WithName("GetSettingsSections")
        .Produces<IReadOnlyList<SettingsSectionSummaryDto>>(StatusCodes.Status200OK);

        group.MapGet("/schema", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var schema = await settingsService.GetSchemaAsync(cancellationToken);
            return Results.Ok(schema);
        })
        .WithName("GetSettingsSchema")
        .Produces<SettingsSchemaDto>(StatusCodes.Status200OK);

        group.MapGet("/readiness", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var readiness = await settingsService.GetReadinessAsync(cancellationToken);
            return Results.Ok(readiness);
        })
        .WithName("GetSettingsReadiness")
        .Produces<SettingsReadinessDto>(StatusCodes.Status200OK);

        group.MapGet("/sections/{sectionName}", async (
            string sectionName,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var section = await settingsService.GetSectionAsync(sectionName, cancellationToken);
            return section == null ? Results.NotFound() : Results.Ok(section);
        })
        .WithName("GetSettingsSection")
        .Produces<SettingsSectionDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/sections/{sectionName}", async (
            string sectionName,
            [FromBody] UpdateSettingsSectionRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var result = await settingsService.UpdateSectionAsync(sectionName, request, cancellationToken);
            if (result.ConcurrencyConflict)
            {
                return Results.Conflict(new { error = "Section was modified by another request. Refresh and try again." });
            }

            if (result.ValidationErrors.Count > 0)
            {
                return Results.BadRequest(new { errors = result.ValidationErrors });
            }

            return result.Section == null ? Results.NotFound() : Results.Ok(result.Section);
        })
        .WithName("UpdateSettingsSection")
        .Produces<SettingsSectionDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        const string chatDefaultsSectionName = "ChatDefaults";

        group.MapGet("/chat-defaults", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var section = await settingsService.GetSectionAsync(chatDefaultsSectionName, cancellationToken);
            return section is null
                ? Results.NotFound()
                : Results.Ok(MapChatDefaults(section));
        })
        .WithName("GetChatDefaults")
        .Produces<ChatDefaultsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/chat-defaults", async (
            [FromBody] UpdateChatDefaultsRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var update = new UpdateSettingsSectionRequest(request.RowVersion, BuildChatDefaultsPayload(request));
            var result = await settingsService.UpdateSectionAsync(chatDefaultsSectionName, update, cancellationToken);
            if (result.ConcurrencyConflict)
            {
                return Results.Conflict(new { error = "Section was modified by another request. Refresh and try again." });
            }

            if (result.ValidationErrors.Count > 0)
            {
                return Results.BadRequest(new { errors = result.ValidationErrors });
            }

            return result.Section is null ? Results.NotFound() : Results.Ok(MapChatDefaults(result.Section));
        })
        .WithName("UpdateChatDefaults")
        .Produces<ChatDefaultsDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);
        
        group.MapPost("/embeddings/rebuild", async (
            IEmbeddingsRebuildService rebuildService,
            CancellationToken cancellationToken) =>
        {
            var result = await rebuildService.RequestRebuildAsync(cancellationToken);
            if (!result.Enqueued)
            {
                return Results.Conflict(new EmbeddingsRebuildConflictResponse(
                    Error: "An embeddings rebuild job is already pending or processing.",
                    JobId: result.JobId,
                    Status: result.Status));
            }

            return Results.Accepted(
                $"/api/settings/embeddings/rebuild/{result.JobId}",
                new EmbeddingsRebuildResponse(result.JobId, result.Status));
        })
        .WithName("RebuildEmbeddings")
        .Produces<EmbeddingsRebuildResponse>(StatusCodes.Status202Accepted)
        .Produces<EmbeddingsRebuildConflictResponse>(StatusCodes.Status409Conflict);

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

        group.MapPost("/models:add", async (
            [FromBody] AddModelRequest request,
            IApplicationSettingsService settingsService,
            IChatTargetValidator chatTargetValidator,
            IRuntimeProfileResolver runtimeProfileResolver,
            ILlamaRuntimeInventoryService inventoryService,
            IHuggingFaceModelDownloadService downloadService,
            IHuggingFaceTokenResolver huggingFaceTokenResolver,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await ValidateAddModelRequestAsync(
                    request,
                    configuration,
                    settingsService,
                    chatTargetValidator,
                    inventoryService,
                    huggingFaceTokenResolver,
                    cancellationToken).ConfigureAwait(false);

                var provider = request.Provider.Trim();
                if (string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
                {
                    var localRuntimeJson = BuildLlamaLocalRuntimeJson(request);
                    var reasoningChoicesJson = await DeriveLlamaReasoningChoicesJsonAsync(
                        runtimeProfileResolver,
                        request.Install!.RuntimeProfileId,
                        cancellationToken).ConfigureAwait(false);

                    var createRequest = BuildModelCreateRequest(
                        request,
                        reasoningChoicesJson,
                        localRuntimeJson);

                    if (string.Equals(request.Install!.Source, "existingAlias", StringComparison.OrdinalIgnoreCase))
                    {
                        var attached = await downloadService.AttachExistingAliasAsync(
                            createRequest,
                            request.Install.RouterModelId,
                            cancellationToken).ConfigureAwait(false);

                        return Results.Ok(new AddModelResponse(
                            OperationId: null,
                            AddOperation: new AddModelOperationDto(
                                Kind: "sync",
                                CatalogModel: attached,
                                Status: "completed",
                                Error: null)));
                    }

                    var op = await downloadService.StartDownloadAsync(
                        BuildStartModelDownloadRequest(request),
                        cancellationToken).ConfigureAwait(false);

                    return Results.Ok(new AddModelResponse(
                        OperationId: op.OperationId,
                        AddOperation: new AddModelOperationDto(
                            Kind: "async",
                            CatalogModel: null,
                            Status: "inProgress",
                            Error: null)));
                }

                var cloudReasoningChoicesJson = await DeriveCloudReasoningChoicesJsonAsync(
                    runtimeProfileResolver,
                    request,
                    cancellationToken).ConfigureAwait(false);

                var created = await settingsService.CreateModelAsync(
                    BuildModelCreateRequest(
                        request,
                        cloudReasoningChoicesJson,
                        runtimeConfigJson: BuildCloudRuntimeConfigJson(request)),
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
                return Results.BadRequest(MapAddModelRoutingError(ex));
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

        group.MapPut("/models/{modelId}", async (
            string modelId,
            [FromBody] UpdateSettingsModelRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.UpdateModelAsync(modelId, request, cancellationToken);
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

        group.MapDelete("/models/{modelId}", async (
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
                var models = await settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
                var model = models.FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.Ordinal));
                if (model is null)
                {
                    return Results.NotFound();
                }

                if (TryGetLlamaRouterModelId(model, out var routerModelId))
                {
                    return await DeleteLlamaRouterEntryAsync(
                        routerModelId,
                        inventoryService,
                        llamaClient,
                        coordinator,
                        adminClient,
                        settingsService,
                        cancellationToken).ConfigureAwait(false);
                }

                var deleted = await settingsService.DeleteModelAsync(modelId, cancellationToken);
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

        group.MapGet("/runtime-profiles", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var profiles = await settingsService.GetRuntimeProfilesAsync(cancellationToken);
            return Results.Ok(profiles);
        })
        .WithName("GetSettingsRuntimeProfiles")
        .Produces<IReadOnlyList<SettingsRuntimeProfileDto>>(StatusCodes.Status200OK);

        group.MapGet("/runtime-profiles/{profileId}", async (
            string profileId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var profile = await settingsService.GetRuntimeProfileAsync(profileId, cancellationToken);
            return profile == null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetSettingsRuntimeProfile")
        .Produces<SettingsRuntimeProfileDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/runtime-profiles", async (
            [FromBody] CreateRuntimeProfileRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var created = await settingsService.CreateRuntimeProfileAsync(request, cancellationToken);
                return Results.Created($"/api/settings/runtime-profiles/{created.ProfileId}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateSettingsRuntimeProfile")
        .Produces<SettingsRuntimeProfileDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest);

        group.MapPut("/runtime-profiles/{profileId}", async (
            string profileId,
            [FromBody] UpdateRuntimeProfileRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.UpdateRuntimeProfileAsync(profileId, request, cancellationToken);
                return updated == null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateSettingsRuntimeProfile")
        .Produces<SettingsRuntimeProfileDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/runtime-profiles/{profileId}", async (
            string profileId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var deleted = await settingsService.DeleteRuntimeProfileAsync(profileId, cancellationToken);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (DbUpdateException ex)
            {
                return Results.BadRequest(new { error = ex.InnerException?.Message ?? ex.Message });
            }
        })
        .WithName("DeleteSettingsRuntimeProfile")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        var serviceEditorsGroup = app.MapGroup("/api/settings/services")
            .WithTags("SettingsServiceEditors")
            .WithOpenApi();

        // See LocalServiceAdminRouting for the admin base/prefix rules and
        // request-body validation helpers used by the local-models proxy
        // endpoints. The functions are extracted out of this method so tests
        // can exercise them directly.
        static string? ResolveLocalServiceAdminBase(string serviceId, IConfiguration configuration) =>
            LocalServiceAdminRouting.ResolveAdminBase(serviceId, configuration);

        static bool TryGetNonEmptyString(JsonElement payload, string propertyName, out string value) =>
            LocalServiceAdminRouting.TryGetNonEmptyString(payload, propertyName, out value);

        static IResult LocalServiceUnavailable(string serviceId)
        {
            var displayName = serviceId switch
            {
                "SpeechTranscription" => "local ASR server",
                "SpeechSynthesis" => "local TTS server",
                "Embeddings" => "local embeddings server",
                "ImageGeneration" => "local image-generation server",
                _ => "local service server",
            };

            return Results.Json(
                new
                {
                    error = $"No {displayName} is configured for this container yet.",
                    code = "LOCAL_SERVICE_UNAVAILABLE",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        serviceEditorsGroup.MapGet("/{serviceId}", async (
            string serviceId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var state = await settingsService.GetServiceEditorStateAsync(serviceId, cancellationToken);
                return Results.Ok(state);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetServiceEditorState")
        .Produces<ServiceEditorStateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapGet("/{serviceId}/readiness", async (
            string serviceId,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var readiness = await settingsService.GetServiceEditorReadinessAsync(serviceId, cancellationToken);
                return Results.Ok(readiness);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetServiceEditorReadiness")
        .Produces<ServiceEditorReadinessDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapPut("/{serviceId}/active-provider", async (
            string serviceId,
            [FromBody] SetActiveProviderRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.SetServiceActiveProviderAsync(serviceId, request.ProviderId, cancellationToken);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("SetServiceActiveProvider")
        .Produces<ServiceEditorStateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapPut("/{serviceId}/providers/{providerId}", async (
            string serviceId,
            string providerId,
            [FromBody] ProviderFieldsUpdateRequest request,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await settingsService.UpdateServiceProviderFieldsAsync(
                    serviceId,
                    providerId,
                    request,
                    cancellationToken);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("UpdateServiceProviderFields")
        .Produces<ServiceEditorStateDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        serviceEditorsGroup.MapGet("/{serviceId}/local-models", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? "/admin/bundles"
                : "/admin/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("GetServiceLocalModels");

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
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
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
            CancellationToken cancellationToken) =>
        {
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }

            if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
            {
                // The bundle contract is strict on purpose: each role is a
                // (repo, single filename) pair, all required. There are no
                // defaults, no optional fallbacks, and no globs — the caller
                // must name exactly one file per role so a bundle download
                // cannot accidentally pull a whole multi-quantization repo.
                var requiredRepoFields = new (string Name, string Description)[]
                {
                    ("bundle_id", "a local id for the bundle on disk"),
                    ("diffusion_repo", "Hugging Face repo id for the diffusion weights"),
                    ("diffusion_file", "single filename of the diffusion file inside the diffusion repo"),
                    ("vae_repo", "Hugging Face repo id for the VAE"),
                    ("vae_file", "single filename of the VAE file inside the VAE repo"),
                    ("text_encoder_repo", "Hugging Face repo id for the text encoder / LLM"),
                    ("text_encoder_file", "single filename of the text encoder file inside the text encoder repo"),
                };
                foreach (var (name, description) in requiredRepoFields)
                {
                    if (!TryGetNonEmptyString(payload, name, out _))
                    {
                        return Results.BadRequest(new
                        {
                            error = $"{name} is required ({description}).",
                        });
                    }
                }

                // Reject glob metacharacters in the filename fields. The
                // upstream SD service uses huggingface_hub allow_patterns
                // literally; a stray '*' would silently widen the download
                // to every matching file in the repo.
                var filenameFields = new[] { "diffusion_file", "vae_file", "text_encoder_file" };
                foreach (var field in filenameFields)
                {
                    if (TryGetNonEmptyString(payload, field, out var filename)
                        && (filename.Contains('*') || filename.Contains('?')))
                    {
                        return Results.BadRequest(new
                        {
                            error = $"{field} must be a single filename (no '*' or '?' glob metacharacters).",
                        });
                    }
                }
            }
            else
            {
                if (!TryGetNonEmptyString(payload, "model_id", out _))
                {
                    return Results.BadRequest(new { error = "model_id is required (Hugging Face repo id)." });
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
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
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
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? $"/admin/bundles/operations/{Uri.EscapeDataString(operationId)}/cancel"
                : $"/admin/models/{Uri.EscapeDataString(operationId)}/cancel";

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("CancelServiceLocalModelOperation");

        // Load / activate a model for ASR, TTS, or Image Generation.
        //
        // ASR / TTS: the request body must carry model_id or model_path plus
        //            optional runtime knobs, which the sub-service loads into
        //            memory. The HF token is stamped in because model_id can
        //            trigger an implicit snapshot download.
        // Image Generation: the active bundle is authoritative on disk (set
        //            via /local-models/{bundleId}/select-active), so the load
        //            endpoint only tells the SD service to start / ensure the
        //            sd-server subprocess is running against that bundle. The
        //            request body is ignored.
        serviceEditorsGroup.MapPost("/{serviceId}/local-models/load", async (
            string serviceId,
            [FromBody] JsonElement payload,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IHuggingFaceTokenResolver hfTokenResolver,
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

            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }

            HttpContent? content = null;
            if (isAsr || isTts)
            {
                var hasModelId = TryGetNonEmptyString(payload, "model_id", out _);
                var hasModelPath = TryGetNonEmptyString(payload, "model_path", out _);
                if (!hasModelId && !hasModelPath)
                {
                    return Results.BadRequest(new { error = "Either model_id or model_path is required." });
                }

                // The load path can trigger an implicit Hugging Face snapshot
                // download (when the caller gives a model_id instead of a
                // path), so the single resolved HF token is stamped in here
                // too.
                var resolvedHfToken = hfTokenResolver.Resolve();
                content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, resolvedHfToken);
            }
            else if (isEmbeddings)
            {
                // Embeddings follows the same explicit contract as ASR/TTS:
                // callers must provide model_id or model_path. No silent
                // default-target load is allowed at this proxy layer.
                var hasModelId = TryGetNonEmptyString(payload, "model_id", out _);
                var hasModelPath = TryGetNonEmptyString(payload, "model_path", out _);
                if (!hasModelId && !hasModelPath)
                {
                    return Results.BadRequest(new { error = "Either model_id or model_path is required." });
                }

                // The load path can still trigger an implicit Hugging Face
                // snapshot download when model_id is used, so stamp the single
                // resolved token into the forwarded body.
                var resolvedHfToken = hfTokenResolver.Resolve();
                content = LocalServiceAdminRouting.BuildForwardedBodyWithHfToken(payload, resolvedHfToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}/admin/load")
            {
                Content = content,
            };
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("LoadServiceLocalModel");

        // Unload the currently-loaded local model / engine. Used by the
        // settings UI to force a release of GPU / RAM without restarting the
        // service container. Image Generation and Embeddings implement
        // /admin/unload at the sub-service layer; ASR / TTS return whatever
        // their upstream returns (typically 404) until they grow the same
        // lifecycle hook.
        serviceEditorsGroup.MapPost("/{serviceId}/local-models/unload", async (
            string serviceId,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}/admin/unload");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("UnloadServiceLocalModel");

        serviceEditorsGroup.MapPost("/{serviceId}/local-models/{modelRef}/select-active", async (
            string serviceId,
            string modelRef,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }

            HttpRequestMessage request;
            if (string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal))
            {
                request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{adminBase}/admin/bundles/{Uri.EscapeDataString(modelRef)}/select-active");
            }
            else
            {
                // Convenience shortcut: "activate this model ref with no extra
                // runtime overrides". Callers that need dtype/device_map/etc
                // must use POST /local-models/load with a full body.
                request = new HttpRequestMessage(HttpMethod.Post, $"{adminBase}/admin/load")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { model_path = modelRef }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }

            using (request)
            {
                return await LocalServiceAdminRouting.ProxyAsync(
                    httpClientFactory.CreateClient(), request, cancellationToken);
            }
        })
        .WithName("SelectServiceLocalModel");

        serviceEditorsGroup.MapGet("/{serviceId}/local-models/{modelRef}", async (
            string serviceId,
            string modelRef,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
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
            var adminBase = ResolveLocalServiceAdminBase(serviceId, configuration);
            if (string.IsNullOrWhiteSpace(adminBase))
            {
                return LocalServiceUnavailable(serviceId);
            }

            var path = string.Equals(serviceId, "ImageGeneration", StringComparison.Ordinal)
                ? $"/admin/bundles/{Uri.EscapeDataString(modelRef)}"
                : $"/admin/models/{Uri.EscapeDataString(modelRef)}";
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{adminBase}{path}");
            return await LocalServiceAdminRouting.ProxyAsync(
                httpClientFactory.CreateClient(), request, cancellationToken);
        })
        .WithName("DeleteServiceLocalModel");

        // Non-chat routing mode matrix — Phase A.5.
        var routingGroup = app.MapGroup("/api/settings/routing")
            .WithTags("SettingsRouting")
            .WithOpenApi();

        routingGroup.MapGet("/chat-targets", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var targets = await settingsService.GetChatTargetsAsync(cancellationToken);
            return Results.Ok(targets);
        })
        .WithName("GetRoutingChatTargets")
        .Produces<IReadOnlyList<ChatTargetDto>>(StatusCodes.Status200OK);

        // Phase B (R-5.2 / R-7.1 / R-9.2) readiness preflight, with the Phase H.1
        // contract fix applied for R-2.4.
        //
        // Behavior matrix:
        //   - modeId blank/absent: probe the service's default mode ("what's the
        //     readiness of what a caller would get if they didn't specify a mode?").
        //     200 + ModeReadinessDto; if no default mode exists the probe surfaces
        //     it as a blocked readiness row, not a 4xx, because that's a
        //     configuration-state question rather than caller input.
        //   - modeId provided and resolves: 200 + ModeReadinessDto (blockers if any).
        //   - modeId provided and unknown: 400 application/problem+json with
        //     ROUTING_MODE_NOT_FOUND (fail-fast per R-2.4 — unknown modeIds are
        //     caller-input errors, never 500 and never a silent blocked DTO).
        routingGroup.MapGet("/preflight", async (
            [FromQuery] string service,
            [FromQuery] string? modeId,
            IApplicationSettingsService settingsService,
            IRoutingReadinessService readiness,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modeId))
                {
                    var modes = await settingsService.GetServiceModesAsync(service, cancellationToken);
                    var defaultMode = modes.FirstOrDefault(m => m.IsDefault) ?? modes.FirstOrDefault();
                    if (defaultMode == null)
                    {
                        return Results.Ok(new ModeReadinessDto(
                            Service: service,
                            ModeId: "(default)",
                            Status: "blocked",
                            Blockers: new[]
                            {
                                $"{RoutingErrorCodes.ModeNotFound}: service '{service}' has no default mode configured."
                            },
                            ProviderSection: null,
                            ModelId: null,
                            RuntimeState: null));
                    }

                    var result = await readiness.ProbeModeAsync(service, defaultMode.ModeId, cancellationToken);
                    return Results.Ok(result);
                }

                var knownModes = await settingsService.GetServiceModesAsync(service, cancellationToken);
                var requested = knownModes.FirstOrDefault(m =>
                    string.Equals(m.ModeId, modeId, StringComparison.Ordinal));
                if (requested == null)
                {
                    throw RoutingException.ModeNotFound(service, modeId!);
                }

                var probe = await readiness.ProbeModeAsync(service, requested.ModeId, cancellationToken);
                return Results.Ok(probe);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetRoutingModePreflight")
        .Produces<ModeReadinessDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status400BadRequest);

        routingGroup.MapGet("/chat-targets/preflight", async (
            ApplicationDbContext db,
            IRoutingReadinessService readiness,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var distinctActive = await GetDistinctActiveAssistantModelIdsAsync(db, cancellationToken);
            var anyAssistantsWithoutModel = await db.Assistants
                .AsNoTracking()
                .AnyAsync(a => a.IsActive && a.ModelId == null, cancellationToken);
            var results = new List<ChatTargetReadinessDto>(distinctActive.Count);
            foreach (var modelId in distinctActive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = InferChatTargetReferenceKind(modelId, configuration, anyAssistantsWithoutModel);
                results.Add(await readiness.ProbeChatTargetAsync(modelId, cancellationToken, kind));
            }

            return Results.Ok((IReadOnlyList<ChatTargetReadinessDto>)results);
        })
        .WithName("GetRoutingChatTargetsPreflight")
        .Produces<IReadOnlyList<ChatTargetReadinessDto>>(StatusCodes.Status200OK);

        // Phase F (R-4.* / R-6.*): per-model readiness probe used by the Models
        // tab to render a row-level badge without fanning out to /preflight.
        // Response shape:
        //   - 200 ChatTargetReadinessDto when the catalog row exists and is ready
        //     (status == "ready"). The same DTO is also returned when the row
        //     exists but is blocked by upstream state — the UI inspects the
        //     blockers list to paint the blocked badge.
        //   - 404 application/problem+json when the catalog row does not exist
        //     (MODEL_NOT_FOUND is the only blocker). Includes the stable
        //     `code` + `action` fields mandated by R-2.2.
        //   - 409 application/problem+json when the model exists but is blocked
        //     on provider / runtime state and the caller asked for a strict
        //     readiness check via `?strict=true`. Default behavior stays 200 so
        //     the Models tab can render blockers inline without handling a
        //     non-2xx response path.
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

            var result = await readiness.ProbeChatTargetAsync(modelId, cancellationToken);

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

        group.MapGet("/overview", async (
            IApplicationSettingsService settingsService,
            IRoutingReadinessService readiness,
            ILlamaRuntimeInventoryService inventoryService,
            ApplicationDbContext db,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var serviceRollups = new List<ServiceModeRollupDto>(RoutedServiceNames.All.Count);
            foreach (var serviceName in RoutedServiceNames.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modes = await settingsService.GetServiceModesAsync(serviceName, cancellationToken);
                var probed = new List<ModeReadinessDto>(modes.Count);
                foreach (var mode in modes)
                {
                    probed.Add(await readiness.ProbeModeAsync(serviceName, mode.ModeId, cancellationToken));
                }

                var ready = probed.Count(m => string.Equals(m.Status, "ready", StringComparison.Ordinal));
                serviceRollups.Add(new ServiceModeRollupDto(
                    Service: serviceName,
                    Ready: ready,
                    Total: probed.Count,
                    Modes: probed));
            }

            var overviewChatTargets = await GetOverviewChatTargetModelIdsAsync(db, cancellationToken);
            var overviewList = overviewChatTargets.ToList();
            var defaultModelId = (configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(defaultModelId)
                && !overviewList.Any(id => string.Equals(id, defaultModelId, StringComparison.Ordinal)))
            {
                overviewList.Add(defaultModelId);
                overviewList.Sort(StringComparer.Ordinal);
            }

            var anyAssistantsWithoutModel = await db.Assistants
                .AsNoTracking()
                .AnyAsync(a => a.IsActive && a.ModelId == null, cancellationToken);
            var chatTargetResults = new List<ChatTargetReadinessDto>(overviewList.Count);
            foreach (var modelId in overviewList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = InferChatTargetReferenceKind(modelId, configuration, anyAssistantsWithoutModel);
                chatTargetResults.Add(await readiness.ProbeChatTargetAsync(modelId, cancellationToken, kind));
            }

            var chatRollup = new ChatTargetsRollupDto(
                Ready: chatTargetResults.Count(t => string.Equals(t.Status, "ready", StringComparison.Ordinal)),
                Total: chatTargetResults.Count,
                Targets: chatTargetResults);

            var readinessDto = await settingsService.GetReadinessAsync(cancellationToken);
            var providerIssues = BuildProviderIssues(readinessDto);

            IReadOnlyList<LlamaRuntimeInventoryItemDto> inventoryItems = [];
            if (RuntimeConfigurationPlaceholders.HasUsableUrl(configuration["LlamaCpp:BaseUrl"]))
            {
                try
                {
                    inventoryItems = await inventoryService.GetInventoryAsync(cancellationToken);
                }
                catch
                {
                    inventoryItems = [];
                }
            }
            var loadedAliases = inventoryItems.Count(i => string.Equals(i.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase));
            var missingArtifacts = inventoryItems
                .Where(i => !i.HasModelFile)
                .Select(i => i.RouterModelId)
                .ToList();

            var runtimeSnapshot = new LlamaRuntimeSnapshotDto(
                LoadedAliases: loadedAliases,
                TotalAliases: inventoryItems.Count,
                MissingArtifactAliases: missingArtifacts);

            return Results.Ok(new SettingsOverviewDto(
                GeneratedUtc: DateTime.UtcNow,
                ServiceModeReadiness: serviceRollups,
                ChatTargets: chatRollup,
                ProviderIssues: providerIssues,
                LlamaRuntime: runtimeSnapshot));
        })
        .WithName("GetSettingsOverview")
        .Produces<SettingsOverviewDto>(StatusCodes.Status200OK);

        // Phase D (R-5.5 / R-8.1): consumers of a single provider connection section.
        group.MapGet("/connections/{section}/usage", async (
            string section,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var usage = await settingsService.GetConnectionUsageAsync(section, cancellationToken);
                return Results.Ok(usage);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("GetConnectionUsage")
        .Produces<ConnectionUsageDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        // Phase E (R-5.7): Infrastructure tab dependency catalog with source
        // tracking (appsettings / environment / compose / user-secrets / unknown),
        // masked values for secret-adjacent keys, and kind discriminator used by
        // the UI to choose between url and path probe strategies.
        group.MapGet("/infrastructure/dependencies", async (
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            var dependencies = await settingsService.GetRuntimeDependenciesAsync(cancellationToken);
            return Results.Ok(dependencies);
        })
        .WithName("GetInfrastructureDependencies")
        .Produces<IReadOnlyList<SettingsRuntimeDependencyDto>>(StatusCodes.Status200OK);

        // Phase E (R-5.7): runtime-owned dependency reachability probes.
        // POST-with-body over GET-with-query-string so a batch of dozens of
        // items cannot push us past browser/proxy URL length limits, and so
        // that future probe kinds (e.g. DNS, socket) can carry their own
        // parameter shapes without re-encoding through query strings.
        group.MapPost("/infrastructure/probes", async (
            [FromBody] InfrastructureProbeRequestDto request,
            GuideAntsApi.Services.Infrastructure.IInfrastructureProbeService probeService,
            CancellationToken cancellationToken) =>
        {
            if (request?.Items is null)
            {
                return Results.BadRequest(new { error = "items is required" });
            }

            var batch = await probeService.ProbeAsync(request.Items, cancellationToken);
            return Results.Ok(batch);
        })
        .WithName("PostInfrastructureProbes")
        .Accepts<InfrastructureProbeRequestDto>("application/json")
        .Produces<InfrastructureProbeBatchDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        var llamaGroup = app.MapGroup("/api/settings/llama")
            .WithTags("SettingsLlama")
            .WithOpenApi();

        static bool HasConfiguredLlamaRuntime(IConfiguration configuration) =>
            RuntimeConfigurationPlaceholders.HasUsableUrl(configuration["LlamaCpp:BaseUrl"]);

        static IResult LlamaRuntimeUnavailable() =>
            Results.Json(
                new
                {
                    error = "No local llama server is configured for this container yet.",
                    code = "LLAMA_RUNTIME_UNAVAILABLE",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        llamaGroup.MapGet("/runtime/inventory", async (
            IConfiguration configuration,
            ILlamaRuntimeInventoryService inventoryService,
            CancellationToken cancellationToken) =>
        {
            if (!HasConfiguredLlamaRuntime(configuration))
            {
                return LlamaRuntimeUnavailable();
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
            CancellationToken cancellationToken) =>
        {
            if (!HasConfiguredLlamaRuntime(configuration))
            {
                return LlamaRuntimeUnavailable();
            }

            // R-12.10 + Phase F (R-6.10): serialize per-alias, AND reject an
            // incoming concurrent request with 409 instead of stalling the caller
            // behind the existing lock. The settings UI relies on this to render
            // an "in progress" pill and avoid a second-click load storm.
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
                await llamaClient.LoadModelAsync(request.RouterModelId, loadParams: null, cancellationToken);
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
            if (!HasConfiguredLlamaRuntime(configuration))
            {
                return LlamaRuntimeUnavailable();
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

        // Phase F (R-6.10): per-alias diagnostic snapshot for the Local Llama
        // Runtime tab. Read-only; uses the inventory service + coordinator
        // lock-state without mutating any state.
        llamaGroup.MapGet("/runtime/status", async (
            IConfiguration configuration,
            ILlamaRuntimeInventoryService inventoryService,
            ILlamaRuntimeCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!HasConfiguredLlamaRuntime(configuration))
            {
                return LlamaRuntimeUnavailable();
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
            [FromBody] StartModelDownloadRequest request,
            IHuggingFaceModelDownloadService downloadService,
            CancellationToken cancellationToken) =>
        {
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
            IHuggingFaceModelDownloadService downloadService,
            CancellationToken cancellationToken) =>
        {
            var op = await downloadService.GetOperationStatusAsync(operationId, cancellationToken);
            return op == null ? Results.NotFound() : Results.Ok(op);
        })
        .WithName("GetLlamaModelDownloadStatus")
        .Produces<ModelDownloadOperationDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Wizard helper: list files in an HF repo so the UI can render a
        // file picker (Model file / Vision projector for llama-cpp; any
        // per-file or preview-only picker for other services) instead of
        // asking the operator to type filenames. Token injection is
        // server-side only; the browser never sees the HF token.

        var huggingFaceGroup = app.MapGroup("/api/settings/huggingface")
            .WithTags("SettingsHuggingFace")
            .WithOpenApi();

        huggingFaceGroup.MapGet("/repositories/{owner}/{repo}/files", (
            string owner,
            string repo,
            HttpRequest request,
            GuideAntsApi.Services.HuggingFace.IHuggingFaceRepositoryBrowser browser,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            HuggingFaceBrowseHandler.ExecuteAsync(
                owner,
                repo,
                request,
                browser,
                loggerFactory,
                cancellationToken))
        .WithName("BrowseHuggingFaceRepository")
        .Produces<GuideAntsApi.Services.HuggingFace.HuggingFaceRepositoryListing>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status502BadGateway);

        llamaGroup.MapDelete("/router/entries/{routerModelId}", async (
            string routerModelId,
            ILlamaRuntimeInventoryService inventoryService,
            ILlamaServerRuntimeClient llamaClient,
            ILlamaRuntimeCoordinator coordinator,
            ILlamaRuntimeAdminClient adminClient,
            IApplicationSettingsService settingsService,
            CancellationToken cancellationToken) =>
        {
            return await DeleteLlamaRouterEntryAsync(
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

    private static bool TryGetLlamaRouterModelId(SettingsModelDto model, out string routerModelId)
    {
        routerModelId = string.Empty;
        if (!string.Equals(model.Provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(model.RuntimeConfigJson))
        {
            return false;
        }

        try
        {
            routerModelId = LocalRuntimeConfigurationParser.Parse(model.ModelId, model.RuntimeConfigJson)
                .RouterModelId
                .Trim();
            return routerModelId.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IResult> DeleteLlamaRouterEntryAsync(
        string routerModelId,
        ILlamaRuntimeInventoryService inventoryService,
        ILlamaServerRuntimeClient llamaClient,
        ILlamaRuntimeCoordinator coordinator,
        ILlamaRuntimeAdminClient adminClient,
        IApplicationSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var trimmed = (routerModelId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Results.BadRequest(new { error = "routerModelId is required" });
        }

        var inventory = await inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var row = inventory.FirstOrDefault(i =>
            string.Equals(i.RouterModelId, trimmed, StringComparison.Ordinal));

        if (row is null)
        {
            return Results.NotFound(new { error = $"No inventory entry for router alias '{trimmed}'." });
        }

        if (row.NotebookReferenceCount > 0)
        {
            return Results.Json(
                new
                {
                    error = "Cannot delete this router alias while notebooks still reference one or more catalog rows that target it.",
                    catalogModelIds = row.CatalogModelIds,
                    notebookReferenceCount = row.NotebookReferenceCount,
                },
                statusCode: StatusCodes.Status409Conflict);
        }

        var handle = coordinator.TryAcquireAliasLock(trimmed);
        if (handle is null)
        {
            var problem = new ProblemDetails
            {
                Type = $"{RoutingProblemDetailsFactory.ProblemTypeBase}runtime-not-ready",
                Title = "Local runtime busy",
                Status = StatusCodes.Status409Conflict,
                Detail = $"A load or unload operation is already in progress for alias '{trimmed}'.",
            };
            problem.Extensions["code"] = RoutingErrorCodes.RuntimeNotReady;
            problem.Extensions["action"] =
                "Wait for the in-flight operation on this alias to complete, then retry.";
            problem.Extensions["modelId"] = trimmed;
            return Results.Problem(problem);
        }

        await using var _ = handle;

        if (string.Equals(row.RuntimeState, "loaded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.RuntimeState, "loading", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await llamaClient.UnloadModelAsync(trimmed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to unload model before delete: {ex.Message}");
            }
        }

        try
        {
            var deleted = await adminClient.DeleteRouterEntryAsync(trimmed, cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                return Results.NotFound(new { error = $"Router alias '{trimmed}' is not registered in llama-admin." });
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        try
        {
            foreach (var modelId in row.CatalogModelIds)
            {
                await settingsService.DeleteModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: $"Router alias '{trimmed}' was deleted, but one or more catalog rows could not be removed: {ex.Message}",
                statusCode: StatusCodes.Status409Conflict);
        }

        return Results.NoContent();
    }

    private static async Task ValidateAddModelRequestAsync(
        AddModelRequest request,
        IConfiguration configuration,
        IApplicationSettingsService settingsService,
        IChatTargetValidator chatTargetValidator,
        ILlamaRuntimeInventoryService inventoryService,
        IHuggingFaceTokenResolver huggingFaceTokenResolver,
        CancellationToken cancellationToken)
    {
        if (request.Catalog is null)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Catalog details are required.",
                remediation: "Fill out the catalog step and try again.");
        }

        var provider = (request.Provider ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(provider) || !ChatTargetValidator.KnownProviders.Contains(provider))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: $"Provider '{request.Provider}' is not supported.",
                remediation: "Pick one of the supported providers and try again.");
        }

        var modelId = (request.Catalog.ModelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Model ID is required.",
                remediation: "Enter a unique catalog model ID in Step 2.");
        }

        var displayName = (request.Catalog.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Display name is required.",
                remediation: "Enter a display name in Step 2.");
        }

        var existingModels = await settingsService.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        if (existingModels.Any(model => string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AddModelException(
                code: "MODEL_ID_TAKEN",
                step: "validation",
                message: $"Model '{modelId}' already exists.",
                remediation: "Back up to Step 2 and choose a different model ID.");
        }

        if (string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
            && !RuntimeConfigurationPlaceholders.HasUsableUrl(configuration["LlamaCpp:BaseUrl"]))
        {
            throw new AddModelException(
                code: "PROVIDER_CREDENTIALS_MISSING",
                step: "validation",
                message: "Provider section 'LlamaCpp' is not ready: no local llama server is configured for this container yet.",
                remediation: "Configure a llama server base URL for this container, or choose a cloud chat provider instead.");
        }

        try
        {
            chatTargetValidator.Validate(new ChatTarget(
                ModelId: modelId,
                Provider: provider,
                RuntimeConfigJson: string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase)
                    ? BuildLlamaLocalRuntimeJson(request)
                    : null));
        }
        catch (RoutingException ex)
        {
            throw MapAddModelRoutingException(ex);
        }

        if (!string.Equals(provider, "llama-cpp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (request.Install is null)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Install details are required for llama-cpp models.",
                remediation: "Complete Step 3 and pick an install source.");
        }

        var source = (request.Install.Source ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Install source is required for llama-cpp models.",
                remediation: "Choose either Hugging Face or Attach existing alias in Step 3.");
        }

        if (string.Equals(source, "huggingface", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(huggingFaceTokenResolver.Resolve()))
            {
                throw new AddModelException(
                    code: "HUGGINGFACE_TOKEN_MISSING",
                    step: "validation",
                    message: "No Hugging Face token is configured.",
                    remediation: "Open Connections → Hugging Face and save a token before retrying.");
            }

            var inventory = await inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
            var existingAlias = inventory.FirstOrDefault(item =>
                string.Equals(item.RouterModelId, request.Install.RouterModelId?.Trim(), StringComparison.Ordinal));
            if (existingAlias is not null && existingAlias.CatalogModelIds.Count > 0)
            {
                throw new AddModelException(
                    code: "ROUTER_ALIAS_TAKEN",
                    step: "validation",
                    message: $"Router alias '{existingAlias.RouterModelId}' is already referenced by catalog rows: {string.Join(", ", existingAlias.CatalogModelIds)}.",
                    remediation: "Back up to Step 3 and choose a different alias.");
            }
        }
    }

    private static CreateSettingsModelRequest BuildModelCreateRequest(
        AddModelRequest request,
        string? reasoningChoicesJson,
        string? runtimeConfigJson)
    {
        return new CreateSettingsModelRequest(
            ModelId: request.Catalog.ModelId.Trim(),
            DisplayName: request.Catalog.DisplayName.Trim(),
            Provider: request.Provider.Trim(),
            Description: string.IsNullOrWhiteSpace(request.Catalog.Description)
                ? null
                : request.Catalog.Description.Trim(),
            ReasoningChoicesJson: reasoningChoicesJson,
            RuntimeConfigJson: runtimeConfigJson,
            IsActive: request.Catalog.IsActive,
            DisplayOrder: request.Catalog.DisplayOrder);
    }

    private static string? BuildCloudRuntimeConfigJson(AddModelRequest request)
    {
        if (request.ProviderConfig == null)
        {
            return null;
        }

        if (!request.ProviderConfig.TryGetPropertyValue("runtimeProfileId", out var profileIdNode)
            || profileIdNode is null)
        {
            return null;
        }

        string? profileId = null;
        if (profileIdNode is System.Text.Json.Nodes.JsonValue strValue
            && strValue.TryGetValue<string>(out var parsedStr))
        {
            profileId = parsedStr?.Trim();
        }

        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        return System.Text.Json.JsonSerializer.Serialize(new { runtimeProfileId = profileId });
    }

    private static string BuildLlamaLocalRuntimeJson(AddModelRequest request)
    {
        if (request.Install is null)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Install details are required for llama-cpp models.",
                remediation: "Complete Step 3 and retry.");
        }

        var routerModelId = (request.Install.RouterModelId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(routerModelId))
        {
            throw new AddModelException(
                code: "ROUTER_ALIAS_TAKEN",
                step: "validation",
                message: "Router alias is required.",
                remediation: "Enter a router alias in Step 3.");
        }

        var runtimeProfileId = (request.Install.RuntimeProfileId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(runtimeProfileId))
        {
            throw new AddModelException(
                code: "RUNTIME_PROFILE_NOT_FOUND",
                step: "validation",
                message: "Runtime profile is required.",
                remediation: "Pick a runtime profile in Step 3.");
        }

        int? ctx = request.Install?.RouterContextSize;
        int? cache = request.Install?.RouterCacheRamMib;

        var config = new LocalRuntimeConfiguration(
            RouterModelId: routerModelId,
            RuntimeProfileId: runtimeProfileId,
            LoadParams: new JsonObject
            {
                ["model"] = routerModelId,
            },
            ParallelToolCalls: false,
            RouterContextSize: ctx,
            RouterCacheRamMib: cache);

        return LocalRuntimeConfigurationParser.SerializeCanonical(config);
    }

    private static StartModelDownloadRequest BuildStartModelDownloadRequest(AddModelRequest request)
    {
        if (request.Install?.HuggingFace is null)
        {
            throw new AddModelException(
                code: "INSTALL_STEP_FAILED",
                step: "validation",
                message: "Hugging Face install details are required.",
                remediation: "Complete the Hugging Face source fields in Step 3.");
        }

        return new StartModelDownloadRequest(
            Repository: request.Install.HuggingFace.Repository.Trim(),
            QuantIncludePattern: request.Install.HuggingFace.QuantIncludePattern.Trim(),
            MmprojIncludePattern: request.Install.HuggingFace.MmprojIncludePattern.Trim(),
            RouterModelId: request.Install.RouterModelId.Trim(),
            TargetDirectory: request.Install.HuggingFace.TargetDirectory.Trim(),
            CatalogModelId: request.Catalog.ModelId.Trim(),
            CatalogDisplayName: request.Catalog.DisplayName.Trim(),
            CatalogRuntimeProfileId: request.Install.RuntimeProfileId.Trim(),
            CatalogDescription: string.IsNullOrWhiteSpace(request.Catalog.Description)
                ? null
                : request.Catalog.Description.Trim(),
            CatalogIsActive: request.Catalog.IsActive,
            CatalogDisplayOrder: request.Catalog.DisplayOrder,
            CatalogLoadParamsJson: JsonSerializer.Serialize(new { model = request.Install.RouterModelId.Trim() }),
            CatalogParallelToolCalls: false,
            CatalogRouterContextSize: request.Install?.RouterContextSize,
            CatalogRouterCacheRamMib: request.Install?.RouterCacheRamMib);
    }

    private static async Task<string?> DeriveLlamaReasoningChoicesJsonAsync(
        IRuntimeProfileResolver runtimeProfileResolver,
        string runtimeProfileId,
        CancellationToken cancellationToken)
    {
        var profile = await runtimeProfileResolver.ResolveAsync(runtimeProfileId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        var choices = profile.ThinkingControl.ChoiceActions.Keys
            .Where(choice => !string.IsNullOrWhiteSpace(choice))
            .Select(choice => choice.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return choices.Count == 0 ? null : JsonSerializer.Serialize(choices);
    }

    private static async Task<string?> DeriveCloudReasoningChoicesJsonAsync(
        IRuntimeProfileResolver runtimeProfileResolver,
        AddModelRequest request,
        CancellationToken cancellationToken)
    {
        var runtimeProfileId = GetProviderConfigString(request.ProviderConfig, "runtimeProfileId");
        if (string.IsNullOrWhiteSpace(runtimeProfileId))
        {
            return null;
        }

        return await DeriveLlamaReasoningChoicesJsonAsync(runtimeProfileResolver, runtimeProfileId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool GetProviderConfigBoolean(JsonObject? providerConfig, string propertyName)
    {
        if (providerConfig is null
            || !providerConfig.TryGetPropertyValue(propertyName, out var node)
            || node is null)
        {
            return false;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var parsedBool))
        {
            return parsedBool;
        }

        return bool.TryParse(node.ToJsonString(), out var fallback) && fallback;
    }

    private static string? GetProviderConfigString(JsonObject? providerConfig, string propertyName)
    {
        if (providerConfig is null
            || !providerConfig.TryGetPropertyValue(propertyName, out var node)
            || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var str))
        {
            return str;
        }

        return null;
    }

    private static AddModelErrorDto MapAddModelRoutingError(RoutingException exception)
        => MapAddModelRoutingException(exception).ToDto();

    private static AddModelException MapAddModelRoutingException(RoutingException exception)
    {
        var code = exception.Code switch
        {
            RoutingErrorCodes.ProviderNotReady => "PROVIDER_CREDENTIALS_MISSING",
            RoutingErrorCodes.RuntimeNotReady => "RUNTIME_PROFILE_NOT_FOUND",
            _ => "INSTALL_STEP_FAILED",
        };

        return new AddModelException(
            code: code,
            step: "validation",
            message: exception.Message,
            remediation: exception.Action,
            innerException: exception);
    }

    /// <summary>R-9.2 <see cref="ChatTargetReadinessDto.ReferenceKind"/> for overview / batch preflight probes.</summary>
    private static string InferChatTargetReferenceKind(
        string modelId,
        IConfiguration configuration,
        bool anyAssistantsWithoutModel)
    {
        var defaultId = (configuration["ChatDefaults:DefaultModelId"] ?? string.Empty).Trim();
        var overrideAll = configuration.GetValue<bool>("ChatDefaults:OverrideAllChatModels");
        if (overrideAll
            && !string.IsNullOrEmpty(defaultId)
            && string.Equals(modelId, defaultId, StringComparison.Ordinal))
        {
            return "overriddenToDefault";
        }

        if (!overrideAll
            && anyAssistantsWithoutModel
            && !string.IsNullOrEmpty(defaultId)
            && string.Equals(modelId, defaultId, StringComparison.Ordinal))
        {
            return "defaultedTo";
        }

        return "direct";
    }

    /// <summary>
    /// Returns the distinct set of catalog model ids referenced by at least one
    /// active assistant (R-9.2 acceptance gate). Deduplicated and ordered for
    /// stable wire output.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetDistinctActiveAssistantModelIdsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        return await db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId != null)
            .Select(a => a.ModelId!)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Overview chat readiness should include:
    /// 1) any model referenced by an active assistant (even if stale/missing),
    /// 2) every active chat-capable catalog model (even if currently unused).
    /// This keeps the Overview list complete while preserving assistant counts.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetOverviewChatTargetModelIdsAsync(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var assistantIds = await db.Assistants
            .AsNoTracking()
            .Where(a => a.IsActive && a.ModelId != null)
            .Select(a => a.ModelId!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var activeCatalogChatIds = await db.Models
            .AsNoTracking()
            .Where(m => m.IsActive && (
                m.Provider == "openai-chat" ||
                m.Provider == "openai-responses" ||
                m.Provider == "azure-openai-chat" ||
                m.Provider == "azure-openai-responses" ||
                m.Provider == "anthropic" ||
                m.Provider == "llama-cpp"))
            .Select(m => m.ModelId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in assistantIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                merged.Add(id.Trim());
            }
        }

        foreach (var id in activeCatalogChatIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                merged.Add(id.Trim());
            }
        }

        return merged
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Parses <see cref="SettingsReadinessDto.GlobalBlockers"/> into per-section
    /// groupings for the Overview payload. Blockers are already of the form
    /// "Section:Field ...", so the section is the token before the first ':'.
    /// </summary>
    private static IReadOnlyList<ProviderConnectionIssueDto> BuildProviderIssues(SettingsReadinessDto readiness)
    {
        var all = readiness.Services.SelectMany(s => s.Blockers)
            .Concat(readiness.GlobalBlockers)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var bySection = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var blocker in all)
        {
            var colonIndex = blocker.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var section = blocker[..colonIndex];
            if (!bySection.TryGetValue(section, out var list))
            {
                list = new List<string>();
                bySection[section] = list;
            }

            list.Add(blocker);
        }

        return bySection
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ProviderConnectionIssueDto(pair.Key, pair.Value))
            .ToList();
    }

    private static ChatDefaultsDto MapChatDefaults(SettingsSectionDto section)
    {
        var p = section.Payload;
        return new ChatDefaultsDto(
            GetPayloadString(p, "DefaultModelId"),
            GetPayloadBool(p, "OverrideAllChatModels", false),
            GetPayloadDouble(p, "Temperature"),
            GetPayloadDouble(p, "TopP"),
            GetPayloadString(p, "ReasoningEffort"),
            GetPayloadString(p, "SamplingParametersJson"),
            section.RowVersion);
    }

    private static JsonObject BuildChatDefaultsPayload(UpdateChatDefaultsRequest request)
    {
        return new JsonObject
        {
            ["DefaultModelId"] = JsonValue.Create(request.DefaultModelId),
            ["OverrideAllChatModels"] = JsonValue.Create(request.OverrideAllChatModels),
            ["Temperature"] = JsonValue.Create(request.Temperature),
            ["TopP"] = JsonValue.Create(request.TopP),
            ["ReasoningEffort"] = JsonValue.Create(request.ReasoningEffort),
            ["SamplingParametersJson"] = JsonValue.Create(request.SamplingParametersJson)
        };
    }

    private static string? GetPayloadString(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node switch
        {
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            _ => null
        };
    }

    private static bool GetPayloadBool(JsonObject payload, string name, bool defaultValue)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is not JsonValue jv)
        {
            return defaultValue;
        }

        return jv.TryGetValue<bool>(out var b) ? b : defaultValue;
    }

    private static double? GetPayloadDouble(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var node) || node is not JsonValue jv)
        {
            return null;
        }

        if (jv.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (jv.TryGetValue<long>(out var l))
        {
            return l;
        }

        return null;
    }
}
