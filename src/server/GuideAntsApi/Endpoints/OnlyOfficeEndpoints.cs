using GuideAntsApi.Configuration;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;

namespace GuideAntsApi.Endpoints;

public static class OnlyOfficeEndpoints
{
    public static void MapOnlyOfficeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/onlyoffice")
            .WithTags("OnlyOffice")
            .WithOpenApi();

        group.MapGet("/capabilities", (
            HttpContext httpContext,
            IOptions<OnlyOfficeOptions> options,
            IOnlyOfficeService service,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("OnlyOfficeEndpoints");
            var onlyOfficeOptions = options.Value;
            logger.LogInformation(
                "ONLYOFFICE capabilities requested. enabled={Enabled} publicUrl={PublicUrl}",
                onlyOfficeOptions.Enabled,
                onlyOfficeOptions.PublicUrl);
            return Results.Ok(new
            {
                enabled = onlyOfficeOptions.Enabled,
                publicUrl = onlyOfficeOptions.PublicUrl,
                supportedExtensions = service.SupportedExtensions,
                supportedContentTypes = service.SupportedContentTypes
            });
        })
        .WithName("GetOnlyOfficeCapabilities")
        .Produces(StatusCodes.Status200OK);

        group.MapPost("/editor-config", async (
            [FromBody] OnlyOfficeEditorConfigRequest request,
            HttpContext httpContext,
            IOptions<OnlyOfficeOptions> options,
            IOnlyOfficeService service,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("OnlyOfficeEndpoints");
            logger.LogInformation(
                "ONLYOFFICE editor-config requested. scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId} canEdit={CanEdit}",
                request.Scope,
                request.ProjectId,
                request.FileId,
                request.NotebookId,
                request.CanEdit);
            if (!options.Value.Enabled)
            {
                logger.LogWarning("ONLYOFFICE editor-config rejected because ONLYOFFICE is disabled.");
                return Results.NotFound(new { message = "ONLYOFFICE is disabled." });
            }

            try
            {
                var config = await service.BuildEditorConfigAsync(httpContext, request, cancellationToken);
                return Results.Ok(new
                {
                    documentServerUrl = config.DocumentServerUrl,
                    config = config.Config
                });
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "ONLYOFFICE editor-config request failed. scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId}",
                    request.Scope, request.ProjectId, request.FileId, request.NotebookId);
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("CreateOnlyOfficeEditorConfig")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/download", async (
            [FromQuery] string? token,
            [FromQuery] string? scope,
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? fileId,
            [FromQuery] Guid? notebookId,
            [FromQuery] int? versionNumber,
            HttpContext httpContext,
            IOnlyOfficeService service,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("OnlyOfficeEndpoints");
            logger.LogInformation(
                "ONLYOFFICE download requested. tokenLength={TokenLength} scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId} versionNumber={VersionNumber}",
                token?.Length ?? 0,
                scope,
                projectId,
                fileId,
                notebookId,
                versionNumber);
            try
            {
                var result = await service.GetDownloadAsync(token, scope, projectId, fileId, notebookId, versionNumber, cancellationToken);
                if (result == null)
                {
                    logger.LogWarning("ONLYOFFICE download target not found.");
                    return Results.NotFound(new { message = "ONLYOFFICE download target was not found." });
                }

                return Results.File(result.Stream, result.ContentType, result.FileName);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "ONLYOFFICE download request failed.");
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("OnlyOfficeDownload")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/callback", async (
            [FromQuery] string? token,
            [FromQuery] string? scope,
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? fileId,
            [FromQuery] Guid? notebookId,
            [FromBody] OnlyOfficeCallbackPayload payload,
            HttpContext httpContext,
            IOnlyOfficeService service,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("OnlyOfficeEndpoints");
            logger.LogInformation(
                "ONLYOFFICE callback requested. status={Status} hasUrl={HasUrl} tokenLength={TokenLength}",
                payload.Status,
                !string.IsNullOrWhiteSpace(payload.Url),
                token?.Length ?? 0);
            try
            {
                await service.HandleCallbackAsync(token, scope, projectId, fileId, notebookId, payload, cancellationToken);
                return Results.Ok(new { error = 0 });
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "ONLYOFFICE callback rejected. status={Status}", payload.Status);
                return Results.Ok(new { error = 1, message = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ONLYOFFICE callback failed unexpectedly. status={Status}", payload.Status);
                return Results.Ok(new { error = 1, message = "ONLYOFFICE callback failed unexpectedly." });
            }
        })
        .WithName("OnlyOfficeCallback")
        .Produces(StatusCodes.Status200OK);

        group.MapPost("/diagnostics/probe", async (
            [FromBody] OnlyOfficeEditorConfigRequest request,
            HttpContext httpContext,
            IOptions<OnlyOfficeOptions> options,
            IOnlyOfficeService service,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("OnlyOfficeEndpoints");
            var onlyOffice = options.Value;

            string? documentUrl = null;
            string? callbackUrl = null;
            string? editorConfigError = null;

            try
            {
                var editorConfig = await service.BuildEditorConfigAsync(httpContext, request, cancellationToken);
                if (TryReadEditorUrl(editorConfig.Config, "document", "url", out var resolvedDocumentUrl))
                {
                    documentUrl = resolvedDocumentUrl;
                }

                if (TryReadEditorUrl(editorConfig.Config, "editorConfig", "callbackUrl", out var resolvedCallbackUrl))
                {
                    callbackUrl = resolvedCallbackUrl;
                }
            }
            catch (Exception ex)
            {
                editorConfigError = ex.Message;
            }

            // /info/info.json is blocked in some deployments; use the editor API script instead.
            var internalInfoUrl = $"{onlyOffice.InternalUrl.TrimEnd('/')}/web-apps/apps/api/documents/api.js";
            var documentServerReachable = false;
            int? documentServerStatusCode = null;
            string? documentServerError = null;

            try
            {
                using var client = httpClientFactory.CreateClient();
                using var response = await client.GetAsync(internalInfoUrl, cancellationToken);
                documentServerStatusCode = (int)response.StatusCode;
                documentServerReachable = response.IsSuccessStatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    documentServerError = $"Document server healthcheck returned HTTP {(int)response.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                documentServerError = ex.Message;
            }

            logger.LogInformation(
                "ONLYOFFICE diagnostics probe. enabled={Enabled} apiBaseUrl={ApiBaseUrl} internalUrl={InternalUrl} publicUrl={PublicUrl} tokenProtection={TokenProtection} jwtEnabled={JwtEnabled} dsReachable={DocumentServerReachable} dsStatus={DocumentServerStatusCode} documentUrl={DocumentUrl} callbackUrl={CallbackUrl} editorConfigError={EditorConfigError}",
                onlyOffice.Enabled,
                onlyOffice.ApiBaseUrl,
                onlyOffice.InternalUrl,
                onlyOffice.PublicUrl,
                "aspnet-data-protection",
                onlyOffice.JwtEnabled,
                documentServerReachable,
                documentServerStatusCode,
                documentUrl,
                callbackUrl,
                editorConfigError);

            return Results.Ok(new
            {
                enabled = onlyOffice.Enabled,
                apiBaseUrl = onlyOffice.ApiBaseUrl,
                internalUrl = onlyOffice.InternalUrl,
                publicUrl = onlyOffice.PublicUrl,
                tokenProtection = "aspnet-data-protection",
                jwtEnabled = onlyOffice.JwtEnabled,
                documentServer = new
                {
                    probeUrl = internalInfoUrl,
                    reachable = documentServerReachable,
                    statusCode = documentServerStatusCode,
                    error = documentServerError
                },
                generated = new
                {
                    documentUrl,
                    callbackUrl,
                    error = editorConfigError
                }
            });
        })
        .WithName("OnlyOfficeDiagnosticsProbe")
        .Produces(StatusCodes.Status200OK);
    }

    private static bool TryReadEditorUrl(object config, string sectionName, string keyName, out string? value)
    {
        value = null;
        if (config is not Dictionary<string, object?> root)
        {
            return false;
        }

        if (!root.TryGetValue(sectionName, out var sectionObj) || sectionObj is not Dictionary<string, object?> section)
        {
            return false;
        }

        if (!section.TryGetValue(keyName, out var valueObj))
        {
            return false;
        }

        var raw = valueObj?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw;
        return true;
    }
}
