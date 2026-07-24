using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuideAntsApi.Configuration;
using GuideAntsApi.DataModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components;

public sealed class DocumentServerService : IDocumentServerService
{
    private const string DocumentServerProxyPublicPrefix = "/api/documentserver/ds";

    private static readonly IReadOnlyCollection<string> SupportedFileExtensions = new[]
    {
        "csv", "doc", "docm", "docx", "dot", "dotm", "dotx", "epub", "fb2", "htm", "html",
        "odp", "ods", "odt", "pdf", "pot", "potm", "potx", "pps", "ppsm", "ppsx", "ppt",
        "pptm", "pptx", "rtf", "txt", "xls", "xlsb", "xlsm", "xlsx", "xlt", "xltm", "xltx"
    };

    private static readonly IReadOnlyCollection<string> SupportedMimeTypes = new[]
    {
        "application/epub+zip",
        "application/msword",
        "application/pdf",
        "application/rtf",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.oasis.opendocument.presentation",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.text",
        "text/csv",
        "text/html",
        "text/plain"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IContentFileService _contentFileService;
    private readonly INotebookFileService _notebookFileService;
    private readonly IOptions<DocumentServerOptions> _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DocumentServerService> _logger;

    public DocumentServerService(
        ApplicationDbContext dbContext,
        IContentFileService contentFileService,
        INotebookFileService notebookFileService,
        IOptions<DocumentServerOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<DocumentServerService> logger)
    {
        _dbContext = dbContext;
        _contentFileService = contentFileService;
        _notebookFileService = notebookFileService;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IReadOnlyCollection<string> SupportedExtensions => SupportedFileExtensions;
    public IReadOnlyCollection<string> SupportedContentTypes => SupportedMimeTypes;

    public bool IsSupported(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        if (!SupportedFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return SupportedMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase)
            || string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DocumentServerEditorConfigResult> BuildEditorConfigAsync(
        HttpContext httpContext,
        DocumentServerEditorConfigRequest request,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            throw new InvalidOperationException("DocumentServer is disabled.");
        }

        var context = await ResolveFileContextAsync(request, cancellationToken);
        if (!IsSupported(context.FileName, context.ContentType))
        {
            throw new InvalidOperationException($"File type is not supported: {context.FileName}");
        }

        var apiBaseUrl = ResolveDocumentServerApiBaseUrl();
        string downloadUrl;
        string callbackUrl;
        if (options.JwtEnabled)
        {
            var downloadToken = ProtectPayload(
                new DownloadTokenPayload(
                Scope: request.Scope,
                ProjectId: request.ProjectId,
                FileId: request.FileId,
                NotebookId: request.NotebookId,
                RelativePath: request.RelativePath,
                VersionNumber: context.VersionNumber),
                "download",
                TimeSpan.FromMinutes(10));
            var callbackToken = ProtectPayload(
                new CallbackTokenPayload(
                Scope: request.Scope,
                ProjectId: request.ProjectId,
                FileId: request.FileId,
                NotebookId: request.NotebookId,
                RelativePath: request.RelativePath),
                "callback",
                TimeSpan.FromHours(1));

            downloadUrl = $"{apiBaseUrl}/api/documentserver/download?token={Uri.EscapeDataString(downloadToken)}";
            callbackUrl = $"{apiBaseUrl}/api/documentserver/callback?token={Uri.EscapeDataString(callbackToken)}";
        }
        else
        {
            var queryScope = Uri.EscapeDataString(request.Scope);
            var queryProjectId = Uri.EscapeDataString(request.ProjectId.ToString("D"));
            var queryFileId = request.FileId.HasValue
                ? $"&fileId={Uri.EscapeDataString(request.FileId.Value.ToString("D"))}"
                : string.Empty;
            var notebookSegment = request.NotebookId.HasValue
                ? $"&notebookId={Uri.EscapeDataString(request.NotebookId.Value.ToString("D"))}"
                : string.Empty;
            var relativePathSegment = !string.IsNullOrWhiteSpace(request.RelativePath)
                ? $"&relativePath={Uri.EscapeDataString(request.RelativePath)}"
                : string.Empty;
            var versionSegment = context.VersionNumber.HasValue
                ? $"&versionNumber={context.VersionNumber.Value.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;
            downloadUrl = $"{apiBaseUrl}/api/documentserver/download?scope={queryScope}&projectId={queryProjectId}{queryFileId}{notebookSegment}{relativePathSegment}{versionSegment}";
            callbackUrl = $"{apiBaseUrl}/api/documentserver/callback?scope={queryScope}&projectId={queryProjectId}{queryFileId}{notebookSegment}{relativePathSegment}";
        }
        var documentType = GetDocumentType(context.FileName);
        var key = BuildDocumentKey(context);
        var extension = Path.GetExtension(context.FileName).TrimStart('.').ToLowerInvariant();
        var mode = request.CanEdit ? "edit" : "view";
        var userName = string.IsNullOrWhiteSpace(request.UserName) ? "GuideAnts User" : request.UserName;
        var userId = string.IsNullOrWhiteSpace(request.UserId) ? "guideants-user" : request.UserId;
        _logger.LogInformation(
            "DocumentServer editor-config built. scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId} relativePath={RelativePath} fileName={FileName} documentType={DocumentType} key={DocumentKey} apiBaseUrl={ApiBaseUrl} downloadUrl={DownloadUrl} callbackUrl={CallbackUrl}",
            LogValueSanitizer.Sanitize(request.Scope),
            LogValueSanitizer.Sanitize(request.ProjectId),
            LogValueSanitizer.Sanitize(request.FileId),
            LogValueSanitizer.Sanitize(request.NotebookId),
            LogValueSanitizer.Sanitize(request.RelativePath),
            LogValueSanitizer.Sanitize(context.FileName),
            LogValueSanitizer.Sanitize(documentType),
            LogValueSanitizer.Sanitize(key),
            LogValueSanitizer.Sanitize(apiBaseUrl),
            LogValueSanitizer.Sanitize(downloadUrl),
            LogValueSanitizer.Sanitize(callbackUrl));

        var config = new Dictionary<string, object?>
        {
            ["documentType"] = documentType,
            ["type"] = "desktop",
            ["document"] = new Dictionary<string, object?>
            {
                ["title"] = context.FileName,
                ["fileType"] = extension,
                ["key"] = key,
                ["url"] = downloadUrl,
                ["permissions"] = new Dictionary<string, object?>
                {
                    ["edit"] = request.CanEdit
                }
            },
            ["editorConfig"] = new Dictionary<string, object?>
            {
                ["callbackUrl"] = callbackUrl,
                ["mode"] = mode,
                ["customization"] = new Dictionary<string, object?>
                {
                    ["autosave"] = true,
                    ["forcesave"] = true
                },
                ["user"] = new Dictionary<string, object?>
                {
                    ["id"] = userId,
                    ["name"] = userName
                }
            }
        };

        if (options.JwtEnabled)
        {
            var token = SignJwtPayload(config);
            config["token"] = token;
        }

        var documentServerUrl = DocumentServerUrlResolver.ResolvePublicUrl(options, httpContext);
        if (string.Equals(documentServerUrl, DocumentServerUrlResolver.ProxyPublicPrefix, StringComparison.Ordinal)
            || documentServerUrl.StartsWith('/'))
        {
            throw new InvalidOperationException(
                "Unable to resolve DocumentServer public URL because request scheme or host is missing.");
        }

        return new DocumentServerEditorConfigResult(
            DocumentServerUrl: documentServerUrl,
            Config: config);
    }

    public async Task<DocumentServerDownloadResult?> GetDownloadAsync(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        string? relativePath,
        int? versionNumber,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
        {
            throw new InvalidOperationException("DocumentServer is disabled.");
        }
        var payload = ResolveRequestContext(token, scope, projectId, fileId, notebookId, relativePath, versionNumber, "download");

        if (payload.Scope.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            if (!payload.FileId.HasValue)
            {
                throw new InvalidOperationException("Project download token is missing file identity.");
            }

            var details = await _contentFileService.GetAsync(payload.ProjectId, payload.FileId.Value);
            if (details == null)
            {
                _logger.LogWarning("DocumentServer download target project file was not found. projectId={ProjectId} fileId={FileId}",
                    LogValueSanitizer.Sanitize(payload.ProjectId), LogValueSanitizer.Sanitize(payload.FileId));
                return null;
            }

            var requestedVersion = payload.VersionNumber ?? details.LatestVersion;
            var content = await _contentFileService.GetVersionContentAsync(payload.ProjectId, payload.FileId.Value, requestedVersion);
            if (content == null)
            {
                _logger.LogWarning(
                    "DocumentServer download content missing for project file. projectId={ProjectId} fileId={FileId} versionNumber={VersionNumber}",
                    LogValueSanitizer.Sanitize(payload.ProjectId),
                    LogValueSanitizer.Sanitize(payload.FileId),
                    LogValueSanitizer.Sanitize(requestedVersion));
                return null;
            }

            return new DocumentServerDownloadResult(
                Stream: new MemoryStream(content.Content),
                ContentType: content.ContentType,
                FileName: content.FileName);
        }

        if (!payload.Scope.Equals("notebook", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported DocumentServer scope in download token: {payload.Scope}");
        }

        if (!payload.NotebookId.HasValue)
        {
            throw new InvalidOperationException("Notebook download token is missing notebook identity.");
        }

        if (!string.IsNullOrWhiteSpace(payload.RelativePath))
        {
            var notebookFile = await _notebookFileService.GetFileAsync(
                payload.ProjectId,
                payload.NotebookId.Value,
                payload.RelativePath);
            if (notebookFile == null)
            {
                _logger.LogWarning(
                    "DocumentServer download target notebook path was not found. notebookId={NotebookId} relativePath={RelativePath}",
                    LogValueSanitizer.Sanitize(payload.NotebookId),
                    LogValueSanitizer.Sanitize(payload.RelativePath));
                return null;
            }

            return new DocumentServerDownloadResult(
                Stream: notebookFile.Value.Stream,
                ContentType: notebookFile.Value.ContentType,
                FileName: notebookFile.Value.FileName);
        }

        if (!payload.FileId.HasValue)
        {
            throw new InvalidOperationException("Notebook download token is missing file identity.");
        }

        var notebookContent = await _notebookFileService.GetFileContentStreamAsync(payload.FileId.Value, cancellationToken);
        if (notebookContent == null)
        {
            _logger.LogWarning(
                "DocumentServer download target notebook file was not found. notebookId={NotebookId} fileId={FileId}",
                LogValueSanitizer.Sanitize(payload.NotebookId),
                LogValueSanitizer.Sanitize(payload.FileId));
            return null;
        }

        return new DocumentServerDownloadResult(
            Stream: notebookContent.Value.Stream,
            ContentType: notebookContent.Value.ContentType,
            FileName: notebookContent.Value.FileName);
    }

    public async Task HandleCallbackAsync(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        string? relativePath,
        DocumentServerCallbackPayload payload,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
        {
            throw new InvalidOperationException("DocumentServer is disabled.");
        }

        var callbackContext = ResolveRequestContext(token, scope, projectId, fileId, notebookId, relativePath, null, "callback");
        var isSaveCallback = payload.Status is 2 or 6;
        if (!isSaveCallback || string.IsNullOrWhiteSpace(payload.Url))
        {
            _logger.LogInformation(
            "DocumentServer callback ignored due to non-save status/url. status={Status} hasUrl={HasUrl} scope={Scope} projectId={ProjectId} fileId={FileId}",
            payload.Status,
            !string.IsNullOrWhiteSpace(payload.Url),
            LogValueSanitizer.Sanitize(callbackContext.Scope),
            LogValueSanitizer.Sanitize(callbackContext.ProjectId),
            LogValueSanitizer.Sanitize(callbackContext.FileId));
            return;
        }

        _logger.LogInformation(
            "DocumentServer callback save received. status={Status} scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId}",
            payload.Status,
            LogValueSanitizer.Sanitize(callbackContext.Scope),
            LogValueSanitizer.Sanitize(callbackContext.ProjectId),
            LogValueSanitizer.Sanitize(callbackContext.FileId),
            LogValueSanitizer.Sanitize(callbackContext.NotebookId));

        var editedFileUrl = ResolveDocumentServerDownloadUrl(payload.Url);
        var client = _httpClientFactory.CreateClient();
        await using var editedStream = await client.GetStreamAsync(editedFileUrl, cancellationToken);
        await using var memory = new MemoryStream();
        await editedStream.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;
        _logger.LogInformation(
            "DocumentServer callback downloaded edited document. status={Status} scope={Scope} projectId={ProjectId} fileId={FileId} notebookId={NotebookId} byteLength={ByteLength}",
            payload.Status,
            LogValueSanitizer.Sanitize(callbackContext.Scope),
            LogValueSanitizer.Sanitize(callbackContext.ProjectId),
            LogValueSanitizer.Sanitize(callbackContext.FileId),
            LogValueSanitizer.Sanitize(callbackContext.NotebookId),
            memory.Length);

        if (callbackContext.Scope.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            if (!callbackContext.FileId.HasValue)
            {
                throw new InvalidOperationException("Project callback is missing file identity.");
            }

            var file = await _contentFileService.GetAsync(callbackContext.ProjectId, callbackContext.FileId.Value);
            if (file == null)
            {
                throw new InvalidOperationException("Project file not found for callback.");
            }

            var contentType = file.ContentType;
            var formFile = new FormFile(memory, 0, memory.Length, "file", file.FileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType
            };

            await _contentFileService.UploadFileAsync(callbackContext.ProjectId, formFile, false, file.FolderId);
            _logger.LogInformation(
                "DocumentServer callback uploaded project file version. projectId={ProjectId} fileId={FileId} fileName={FileName} byteLength={ByteLength}",
                LogValueSanitizer.Sanitize(callbackContext.ProjectId),
                LogValueSanitizer.Sanitize(callbackContext.FileId),
                LogValueSanitizer.Sanitize(file.FileName),
                memory.Length);
            return;
        }

        if (!callbackContext.Scope.Equals("notebook", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported DocumentServer scope in callback token: {callbackContext.Scope}");
        }

        if (!callbackContext.NotebookId.HasValue)
        {
            throw new InvalidOperationException("Notebook callback is missing notebook identity.");
        }

        string targetPath;
        if (!string.IsNullOrWhiteSpace(callbackContext.RelativePath))
        {
            targetPath = callbackContext.RelativePath.Replace("\\", "/").TrimStart('/');
            var linkedFile = await _notebookFileService.GetFileAsync(
                callbackContext.ProjectId,
                callbackContext.NotebookId.Value,
                targetPath);
            if (linkedFile == null)
            {
                throw new InvalidOperationException("Notebook file not found for callback.");
            }

            var targetPhysicalPath = (linkedFile.Value.Stream as FileStream)?.Name;
            await linkedFile.Value.Stream.DisposeAsync();
            if (string.IsNullOrWhiteSpace(targetPhysicalPath))
            {
                throw new InvalidOperationException("Notebook callback could not resolve target path.");
            }

            _logger.LogInformation(
                "DocumentServer callback writing linked notebook file by relative path. projectId={ProjectId} notebookId={NotebookId} relativePath={RelativePath} byteLength={ByteLength}",
                callbackContext.ProjectId,
                callbackContext.NotebookId.Value,
                targetPath,
                memory.Length);
            memory.Position = 0;
            await using var destination = new FileStream(targetPhysicalPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await memory.CopyToAsync(destination, cancellationToken);
            return;
        }

        if (!callbackContext.FileId.HasValue)
        {
            throw new InvalidOperationException("Notebook callback is missing file identity.");
        }

        var notebookFile = await _dbContext.NotebookFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                nf => nf.Id == callbackContext.FileId.Value && nf.NotebookId == callbackContext.NotebookId.Value,
                cancellationToken);
        if (notebookFile == null)
        {
            throw new InvalidOperationException("Notebook file not found for callback.");
        }

        targetPath = notebookFile.RelativePath;
        var targetFolder = Path.GetDirectoryName(targetPath.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(targetPath);
        var formFileNotebook = new FormFile(memory, 0, memory.Length, "files", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = InferContentType(fileName)
        };

        _logger.LogInformation(
            "DocumentServer callback uploading notebook file. projectId={ProjectId} notebookId={NotebookId} fileId={FileId} relativePath={RelativePath} targetFolder={TargetFolder} fileName={FileName} byteLength={ByteLength}",
            LogValueSanitizer.Sanitize(callbackContext.ProjectId),
            LogValueSanitizer.Sanitize(callbackContext.NotebookId.Value),
            LogValueSanitizer.Sanitize(callbackContext.FileId),
            LogValueSanitizer.Sanitize(targetPath),
            LogValueSanitizer.Sanitize(targetFolder),
            LogValueSanitizer.Sanitize(fileName),
            memory.Length);
        var files = new FormFileCollection { formFileNotebook };
        await _notebookFileService.UploadFilesAsync(
            callbackContext.ProjectId,
            callbackContext.NotebookId.Value,
            files,
            targetFolder,
            index: false,
            forceMarkdownExtraction: true);
    }

    private async Task<DocumentServerFileContext> ResolveFileContextAsync(
        DocumentServerEditorConfigRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Scope.Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.FileId.HasValue)
            {
                throw new InvalidOperationException("Project scope requires fileId.");
            }

            var file = await _contentFileService.GetAsync(request.ProjectId, request.FileId.Value);
            if (file == null)
            {
                throw new InvalidOperationException("Project file was not found.");
            }

            return new DocumentServerFileContext(
                Scope: request.Scope,
                ProjectId: request.ProjectId,
                FileId: request.FileId,
                NotebookId: null,
                RelativePath: null,
                FileName: file.FileName,
                ContentType: file.ContentType,
                VersionNumber: file.LatestVersion,
                KeyMaterial: $"{file.Id:N}:{file.LatestVersion.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!request.Scope.Equals("notebook", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported DocumentServer scope: {request.Scope}");
        }

        if (!request.NotebookId.HasValue)
        {
            throw new InvalidOperationException("Notebook scope requires notebookId.");
        }

        if (!string.IsNullOrWhiteSpace(request.RelativePath))
        {
            var normalizedRelativePath = request.RelativePath.Replace("\\", "/").TrimStart('/');
            var file = await _notebookFileService.GetFileAsync(
                request.ProjectId,
                request.NotebookId.Value,
                normalizedRelativePath);
            if (file == null)
            {
                throw new InvalidOperationException("Notebook file was not found.");
            }

            await file.Value.Stream.DisposeAsync();

            return new DocumentServerFileContext(
                Scope: request.Scope,
                ProjectId: request.ProjectId,
                FileId: null,
                NotebookId: request.NotebookId,
                RelativePath: normalizedRelativePath,
                FileName: file.Value.FileName,
                ContentType: file.Value.ContentType,
                VersionNumber: null,
                KeyMaterial: $"path:{normalizedRelativePath}:{DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!request.FileId.HasValue)
        {
            throw new InvalidOperationException("Notebook scope requires fileId or relativePath.");
        }

        var notebookFile = await _dbContext.NotebookFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                nf => nf.Id == request.FileId.Value && nf.NotebookId == request.NotebookId.Value,
                cancellationToken);
        if (notebookFile == null)
        {
            throw new InvalidOperationException("Notebook file was not found.");
        }

        var fileName = Path.GetFileName(notebookFile.RelativePath);
        var contentType = InferContentType(fileName);
        var hash = string.IsNullOrWhiteSpace(notebookFile.FileHash)
            ? notebookFile.LastModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture)
            : notebookFile.FileHash;

        return new DocumentServerFileContext(
            Scope: request.Scope,
            ProjectId: request.ProjectId,
            FileId: request.FileId,
            NotebookId: request.NotebookId,
            RelativePath: null,
            FileName: fileName,
            ContentType: contentType,
            VersionNumber: null,
            KeyMaterial: $"{notebookFile.Id:N}:{hash}");
    }

    private string BuildDocumentKey(DocumentServerFileContext context)
    {
        var raw = $"{context.Scope}:{context.ProjectId:N}:{context.KeyMaterial}";
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..40];
    }

    private static string GetDocumentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "xls" or "xlsx" or "xlsb" or "xlsm" or "xlt" or "xltm" or "xltx" or "ods" or "csv" => "cell",
            "ppt" or "pptx" or "pptm" or "pps" or "ppsx" or "ppsm" or "pot" or "potx" or "potm" or "odp" => "slide",
            _ => "word"
        };
    }

    private string ResolveDocumentServerApiBaseUrl()
    {
        var configured = _options.Value.ApiBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("DocumentServer:ApiBaseUrl must be configured.");
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("DocumentServer:ApiBaseUrl must be an absolute URL.");
        }

        return configured.TrimEnd('/');
    }

    private string ResolveDocumentServerDownloadUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
        {
            throw new InvalidOperationException("DocumentServer callback URL must be an absolute URL.");
        }

        var options = _options.Value;
        if (!Uri.TryCreate(options.InternalUrl?.Trim(), UriKind.Absolute, out var internalUri))
        {
            throw new InvalidOperationException("DocumentServer:InternalUrl must be an absolute URL.");
        }

        var rewrittenPath = sourceUri.AbsolutePath;
        if (string.Equals(rewrittenPath, DocumentServerProxyPublicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            rewrittenPath = "/";
        }
        else if (rewrittenPath.StartsWith($"{DocumentServerProxyPublicPrefix}/", StringComparison.OrdinalIgnoreCase))
        {
            rewrittenPath = rewrittenPath[DocumentServerProxyPublicPrefix.Length..];
        }
        else
        {
            return sourceUri.ToString();
        }

        var builder = new UriBuilder(sourceUri)
        {
            Scheme = internalUri.Scheme,
            Host = internalUri.Host,
            Port = internalUri.IsDefaultPort ? -1 : internalUri.Port
        };

        var internalPath = internalUri.AbsolutePath.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(internalPath))
        {
            builder.Path = $"{internalPath}/{rewrittenPath.TrimStart('/')}";
        }
        else
        {
            builder.Path = rewrittenPath;
        }

        var rewrittenUrl = builder.Uri.ToString();
        _logger.LogInformation(
            "DocumentServer callback download URL rewritten from proxied path to internal origin. sourceHost={SourceHost} internalHost={InternalHost}",
            LogValueSanitizer.Sanitize(sourceUri.Authority),
            LogValueSanitizer.Sanitize(internalUri.Authority));

        return rewrittenUrl;
    }

    private string InferContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "csv" => "text/csv",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "htm" or "html" => "text/html",
            "odt" => "application/vnd.oasis.opendocument.text",
            "ods" => "application/vnd.oasis.opendocument.spreadsheet",
            "odp" => "application/vnd.oasis.opendocument.presentation",
            "pdf" => "application/pdf",
            "ppt" => "application/vnd.ms-powerpoint",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "rtf" => "application/rtf",
            "txt" => "text/plain",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };
    }

    private string ProtectPayload<T>(T payload, string purpose, TimeSpan lifetime)
    {
        var envelope = new TokenEnvelope<T>(
            Payload: payload,
            ExpiresUnix: DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds(),
            Purpose: purpose);
        return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(envelope));
    }

    private string SignJwtPayload<T>(T payload)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.JwtSecret))
        {
            throw new InvalidOperationException("DocumentServer:JwtSecret must be configured when DocumentServer:JwtEnabled is true.");
        }

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        }));
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var unsignedToken = $"{header}.{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.JwtSecret));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken)));
        return $"{unsignedToken}.{signature}";
    }

    private T ValidateTokenOrThrow<T>(string token, string purpose) where T : class
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"DocumentServer {purpose} token is missing.");
        }

        TokenEnvelope<T>? envelope;
        try
        {
            var payloadBytes = Base64UrlDecode(token);
            envelope = JsonSerializer.Deserialize<TokenEnvelope<T>>(payloadBytes);
        }
        catch (Exception ex) when (ex is FormatException || ex is JsonException)
        {
            throw new InvalidOperationException($"DocumentServer {purpose} token payload is invalid.", ex);
        }

        if (envelope?.Payload == null)
        {
            throw new InvalidOperationException($"DocumentServer {purpose} token payload was empty.");
        }

        if (!string.Equals(envelope.Purpose, purpose, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"DocumentServer {purpose} token purpose is invalid.");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (envelope.ExpiresUnix < now)
        {
            throw new InvalidOperationException($"DocumentServer {purpose} token has expired.");
        }

        return envelope.Payload;
    }

    private RequestContext ResolveRequestContext(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        string? relativePath,
        int? versionNumber,
        string purpose)
    {
        if (_options.Value.JwtEnabled)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException($"DocumentServer {purpose} token is missing.");
            }

            if (purpose.Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                var download = ValidateTokenOrThrow<DownloadTokenPayload>(token, purpose);
                return new RequestContext(download.Scope, download.ProjectId, download.FileId, download.NotebookId, download.RelativePath, download.VersionNumber);
            }

            var callback = ValidateTokenOrThrow<CallbackTokenPayload>(token, purpose);
            return new RequestContext(callback.Scope, callback.ProjectId, callback.FileId, callback.NotebookId, callback.RelativePath, null);
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new InvalidOperationException($"DocumentServer {purpose} scope is missing.");
        }

        if (!projectId.HasValue)
        {
            throw new InvalidOperationException($"DocumentServer {purpose} identity is missing.");
        }

        var hasFileId = fileId.HasValue;
        var hasRelativePath = !string.IsNullOrWhiteSpace(relativePath);
        if (!hasFileId && !hasRelativePath)
        {
            throw new InvalidOperationException($"DocumentServer {purpose} identity is missing.");
        }

        return new RequestContext(
            Scope: scope,
            ProjectId: projectId.Value,
            FileId: fileId,
            NotebookId: notebookId,
            RelativePath: relativePath,
            VersionNumber: versionNumber);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string encoded)
    {
        var padded = encoded
            .Replace('-', '+')
            .Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder > 0)
        {
            padded = padded.PadRight(padded.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(padded);
    }

    private sealed record DocumentServerFileContext(
        string Scope,
        Guid ProjectId,
        Guid? FileId,
        Guid? NotebookId,
        string? RelativePath,
        string FileName,
        string ContentType,
        int? VersionNumber,
        string KeyMaterial);

    private sealed record DownloadTokenPayload(
        string Scope,
        Guid ProjectId,
        Guid? FileId,
        Guid? NotebookId,
        string? RelativePath,
        int? VersionNumber);

    private sealed record CallbackTokenPayload(
        string Scope,
        Guid ProjectId,
        Guid? FileId,
        Guid? NotebookId,
        string? RelativePath);

    private sealed record TokenEnvelope<T>(
        T Payload,
        long ExpiresUnix,
        string Purpose);

    private sealed record RequestContext(
        string Scope,
        Guid ProjectId,
        Guid? FileId,
        Guid? NotebookId,
        string? RelativePath,
        int? VersionNumber);

}
