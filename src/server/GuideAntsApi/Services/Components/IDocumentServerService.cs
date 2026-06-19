namespace GuideAntsApi.Services.Components;

public interface IDocumentServerService
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
    IReadOnlyCollection<string> SupportedContentTypes { get; }

    bool IsSupported(string fileName, string contentType);

    Task<DocumentServerEditorConfigResult> BuildEditorConfigAsync(
        HttpContext httpContext,
        DocumentServerEditorConfigRequest request,
        CancellationToken cancellationToken);

    Task<DocumentServerDownloadResult?> GetDownloadAsync(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        string? relativePath,
        int? versionNumber,
        CancellationToken cancellationToken);

    Task HandleCallbackAsync(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        string? relativePath,
        DocumentServerCallbackPayload payload,
        CancellationToken cancellationToken);
}

public sealed record DocumentServerEditorConfigRequest(
    string Scope,
    Guid ProjectId,
    Guid? FileId,
    Guid? NotebookId,
    bool CanEdit,
    string? UserId,
    string? UserName,
    string? RelativePath = null);

public sealed record DocumentServerEditorConfigResult(
    string DocumentServerUrl,
    object Config);

public sealed record DocumentServerDownloadResult(
    Stream Stream,
    string ContentType,
    string FileName);

public sealed record DocumentServerCallbackPayload(
    int Status,
    string? Url);
