using GuideAntsApi.Configuration;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace GuideAntsApi.Endpoints;

public static class DocumentServerEndpoints
{
    private const string DocumentServerProxyPathItemKey = "__DocumentServerProxyPath";
    private const string DocumentServerProxyPublicPrefix = "/api/documentserver/ds";

    private static readonly HttpMessageInvoker DocumentServerProxyHttpClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = null
    });

    private static readonly ForwarderRequestConfig DocumentServerProxyRequestConfig = new()
    {
        ActivityTimeout = TimeSpan.FromMinutes(30)
    };

    private static readonly HttpTransformer DocumentServerProxyTransformer = new DocumentServerProxyHttpTransformer();

    public static void MapDocumentServerEndpoints(this WebApplication app)
    {
        app.MapMethods("/api/documentserver/ds/{**path}", [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options], async (
            HttpContext httpContext,
            string? path,
            IOptions<DocumentServerOptions> options,
            IHttpForwarder forwarder,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentServerEndpoints");
            var documentServerOptions = options.Value;
            if (!documentServerOptions.Enabled)
            {
                return Results.NotFound(new { message = "DocumentServer is disabled." });
            }

            if (!Uri.TryCreate(documentServerOptions.InternalUrl?.Trim(), UriKind.Absolute, out var internalUri))
            {
                logger.LogWarning("DocumentServer proxy rejected due to invalid internal URL configuration. internalUrl={InternalUrl}", LogValueSanitizer.Sanitize(documentServerOptions.InternalUrl));
                return Results.BadRequest(new { message = "DocumentServer:InternalUrl must be configured as an absolute URL." });
            }

            httpContext.Items[DocumentServerProxyPathItemKey] = NormalizeProxyPath(path);
            var destinationPrefix = BuildDestinationPrefix(internalUri);
            var proxyError = await forwarder.SendAsync(
                httpContext,
                destinationPrefix,
                DocumentServerProxyHttpClient,
                DocumentServerProxyRequestConfig,
                DocumentServerProxyTransformer);

            if (proxyError == ForwarderError.None)
            {
                return Results.Empty;
            }

            var errorFeature = httpContext.GetForwarderErrorFeature();
            logger.LogWarning(
                errorFeature?.Exception,
                "DocumentServer proxy failed. error={Error} destinationPrefix={DestinationPrefix} path={Path}",
                proxyError,
                destinationPrefix,
                path);
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = StatusCodes.Status502BadGateway;
                await httpContext.Response.WriteAsJsonAsync(new { message = "DocumentServer proxy request failed." });
            }

            return Results.Empty;
        })
        .WithName("DocumentServerProxy")
        .ExcludeFromDescription();

        var group = app.MapGroup("/api/documentserver")
            .WithTags("DocumentServer")
            .WithOpenApi();

        group.MapGet("/capabilities", (
            HttpContext httpContext,
            IOptions<DocumentServerOptions> options,
            IDocumentServerService service,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentServerEndpoints");
            var documentServerOptions = options.Value;
            logger.LogInformation(
                "DocumentServer capabilities requested. enabled={Enabled} publicUrl={PublicUrl}",
                documentServerOptions.Enabled,
                documentServerOptions.PublicUrl);
            return Results.Ok(new
            {
                enabled = documentServerOptions.Enabled,
                publicUrl = documentServerOptions.PublicUrl,
                supportedExtensions = service.SupportedExtensions,
                supportedContentTypes = service.SupportedContentTypes
            });
        })
        .WithName("GetDocumentServerCapabilities")
        .Produces(StatusCodes.Status200OK);

        group.MapPost("/editor-config", async (
            [FromBody] DocumentServerEditorConfigRequest request,
            HttpContext httpContext,
            IOptions<DocumentServerOptions> options,
            IDocumentServerService service,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentServerEndpoints");
            logger.LogInformation(
                "DocumentServer editor-config requested. scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId} canEdit={CanEdit}",
                LogValueSanitizer.Sanitize(request.Scope),
                LogValueSanitizer.Sanitize(request.ProjectId),
                LogValueSanitizer.Sanitize(request.FileId),
                LogValueSanitizer.Sanitize(request.NotebookId),
                request.CanEdit);
            if (!options.Value.Enabled)
            {
                logger.LogWarning("DocumentServer editor-config rejected because DocumentServer is disabled.");
                return Results.NotFound(new { message = "DocumentServer is disabled." });
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
                logger.LogWarning(ex, "DocumentServer editor-config request failed. scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId}",
                    LogValueSanitizer.Sanitize(request.Scope),
                    LogValueSanitizer.Sanitize(request.ProjectId),
                    LogValueSanitizer.Sanitize(request.FileId),
                    LogValueSanitizer.Sanitize(request.NotebookId));
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("CreateDocumentServerEditorConfig")
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
            IDocumentServerService service,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentServerEndpoints");
            logger.LogInformation(
                "DocumentServer download requested. tokenLength={TokenLength} scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId} versionNumber={VersionNumber}",
                token?.Length ?? 0,
                LogValueSanitizer.Sanitize(scope),
                LogValueSanitizer.Sanitize(projectId),
                LogValueSanitizer.Sanitize(fileId),
                LogValueSanitizer.Sanitize(notebookId),
                versionNumber);
            try
            {
                var result = await service.GetDownloadAsync(token, scope, projectId, fileId, notebookId, versionNumber, cancellationToken);
                if (result == null)
                {
                    logger.LogWarning("DocumentServer download target not found.");
                    return Results.NotFound(new { message = "DocumentServer download target was not found." });
                }

                return Results.File(result.Stream, result.ContentType, result.FileName);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "DocumentServer download request failed.");
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("DocumentServerDownload")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/callback", async (
            [FromQuery] string? token,
            [FromQuery] string? scope,
            [FromQuery] Guid? projectId,
            [FromQuery] Guid? fileId,
            [FromQuery] Guid? notebookId,
            [FromBody] DocumentServerCallbackPayload payload,
            HttpContext httpContext,
            IDocumentServerService service,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentServerEndpoints");
            logger.LogInformation(
                "DocumentServer callback requested. status={Status} hasUrl={HasUrl} tokenLength={TokenLength}",
                LogValueSanitizer.Sanitize(payload.Status),
                !string.IsNullOrWhiteSpace(payload.Url),
                token?.Length ?? 0);
            try
            {
                await service.HandleCallbackAsync(token, scope, projectId, fileId, notebookId, payload, cancellationToken);
                return Results.Ok(new { error = 0 });
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "DocumentServer callback rejected. status={Status}", LogValueSanitizer.Sanitize(payload.Status));
                return Results.Ok(new { error = 1, message = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DocumentServer callback failed unexpectedly. status={Status}", LogValueSanitizer.Sanitize(payload.Status));
                return Results.Ok(new { error = 1, message = "DocumentServer callback failed unexpectedly." });
            }
        })
        .WithName("DocumentServerCallback")
        .Produces(StatusCodes.Status200OK);

        group.MapPost("/diagnostics/probe", async (
            [FromBody] DocumentServerEditorConfigRequest request,
            HttpContext httpContext,
            IOptions<DocumentServerOptions> options,
            IDocumentServerService service,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("DocumentServerEndpoints");
            var documentServer = options.Value;

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
            var internalInfoUrl = $"{documentServer.InternalUrl.TrimEnd('/')}/web-apps/apps/api/documents/api.js";
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
                    documentServerError = $"DocumentServer healthcheck returned HTTP {(int)response.StatusCode}.";
                }
            }
            catch (Exception ex)
            {
                documentServerError = ex.Message;
            }

            logger.LogInformation(
                "DocumentServer diagnostics probe. enabled={Enabled} apiBaseUrl={ApiBaseUrl} internalUrl={InternalUrl} publicUrl={PublicUrl} tokenProtection={TokenProtection} jwtEnabled={JwtEnabled} dsReachable={DocumentServerReachable} dsStatus={DocumentServerStatusCode} documentUrl={DocumentUrl} callbackUrl={CallbackUrl} editorConfigError={EditorConfigError}",
                documentServer.Enabled,
                documentServer.ApiBaseUrl,
                documentServer.InternalUrl,
                documentServer.PublicUrl,
                "aspnet-data-protection",
                documentServer.JwtEnabled,
                documentServerReachable,
                documentServerStatusCode,
                documentUrl,
                callbackUrl,
                editorConfigError);

            return Results.Ok(new
            {
                enabled = documentServer.Enabled,
                apiBaseUrl = documentServer.ApiBaseUrl,
                internalUrl = documentServer.InternalUrl,
                publicUrl = documentServer.PublicUrl,
                tokenProtection = "aspnet-data-protection",
                jwtEnabled = documentServer.JwtEnabled,
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
        .WithName("DocumentServerDiagnosticsProbe")
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

    private static string NormalizeProxyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        return "/" + path.TrimStart('/');
    }

    private static string BuildDestinationPrefix(Uri internalUri)
    {
        var basePath = internalUri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(basePath) || string.Equals(basePath, "/", StringComparison.Ordinal))
        {
            return $"{internalUri.Scheme}://{internalUri.Authority}";
        }

        return $"{internalUri.Scheme}://{internalUri.Authority}{basePath}";
    }

    private sealed class DocumentServerProxyHttpTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            var proxyPath = httpContext.Items.TryGetValue(DocumentServerProxyPathItemKey, out var pathObj)
                ? pathObj as string
                : null;
            var normalizedPath = string.IsNullOrWhiteSpace(proxyPath)
                ? "/"
                : proxyPath;
            if (!normalizedPath.StartsWith('/'))
            {
                normalizedPath = "/" + normalizedPath;
            }

            var queryString = httpContext.Request.QueryString.HasValue
                ? httpContext.Request.QueryString.Value
                : string.Empty;
            proxyRequest.RequestUri = new Uri($"{destinationPrefix.TrimEnd('/')}{normalizedPath}{queryString}", UriKind.Absolute);

            if (httpContext.Request.Host.HasValue)
            {
                proxyRequest.Headers.Host = httpContext.Request.Host.Value;
            }

            if (httpContext.Request.Host.HasValue)
            {
                proxyRequest.Headers.Remove("X-Forwarded-Host");
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", httpContext.Request.Host.Value);
            }

            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", httpContext.Request.Scheme);

            proxyRequest.Headers.Remove("X-Forwarded-Prefix");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", DocumentServerProxyPublicPrefix);

            proxyRequest.Headers.Remove("X-Forwarded-PathBase");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-PathBase", DocumentServerProxyPublicPrefix);
        }
    }
}
