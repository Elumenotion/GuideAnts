namespace GuideAntsApi.Services.Components;

public interface IOnlyOfficeService
{
    IReadOnlyCollection<string> SupportedExtensions { get; }
    IReadOnlyCollection<string> SupportedContentTypes { get; }

    bool IsSupported(string fileName, string contentType);

    Task<OnlyOfficeEditorConfigResult> BuildEditorConfigAsync(
        HttpContext httpContext,
        OnlyOfficeEditorConfigRequest request,
        CancellationToken cancellationToken);

    Task<OnlyOfficeDownloadResult?> GetDownloadAsync(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        int? versionNumber,
        CancellationToken cancellationToken);

    Task HandleCallbackAsync(
        string? token,
        string? scope,
        Guid? projectId,
        Guid? fileId,
        Guid? notebookId,
        OnlyOfficeCallbackPayload payload,
        CancellationToken cancellationToken);
}

public sealed record OnlyOfficeEditorConfigRequest(
    string Scope,
    Guid ProjectId,
    Guid FileId,
    Guid? NotebookId,
    bool CanEdit,
    string? UserId,
    string? UserName);

public sealed record OnlyOfficeEditorConfigResult(
    string DocumentServerUrl,
    object Config);

public sealed record OnlyOfficeDownloadResult(
    Stream Stream,
    string ContentType,
    string FileName);

public sealed record OnlyOfficeCallbackPayload(
    int Status,
    string? Url);
