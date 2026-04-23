using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Models;

public record UploadFileRequest(
    IFormFile File,
    bool Index = false
);

public record ContentFileDetailsDto(
    Guid Id,
    string FileName,
    string Path,
    string RelativePath,
    string ContentType,
    bool Index,
    string DocumentId,
    DateTime Created,
    long FileSize,
    Guid? FolderId,
    string? FolderPath,
    int LatestVersion,
    bool IsSnapshot,

    // Markdown extraction information
    bool HasMarkdownShadow,
    MarkdownExtractionStatus? MarkdownStatus,
    DateTime? MarkdownProcessedAt
);

public record ContentFileVersionDto(
    Guid Id,
    int VersionNumber,
    string FileName,
    long FileSize,
    string ContentType,
    DateTime Created,
    bool FromNotebook,
    Guid? OriginNotebookId,
    // NEW: Lineage information showing where version was originally created
    string? OriginalRelativePath,
    Guid? OriginalFolderId,
    string? ContentHash
  );

// Add new DTOs for handling file deletion conflicts
public record FileUsageInNotebookDto(
    Guid NotebookId,
    string NotebookTitle,
    Guid NotebookFileId,
    string FileName,
    string RelativePath
);

public record FileInUseErrorDto(
    string Message,
    IEnumerable<FileUsageInNotebookDto> NotebooksUsingFile
);

// Markdown shadow DTOs
public record ContentFileMarkdownShadowDto(
    Guid Id,
    Guid OriginalContentFileVersionId,
    string ContentHash,
    long FileSize,
    MarkdownExtractionStatus Status,
    string? ErrorMessage,
    DateTime Created,
    DateTime? ProcessedAt
);

public record NotebookFileMarkdownShadowDto(
    Guid Id,
    Guid OriginalNotebookFileId,
    string ContentHash,
    long FileSize,
    MarkdownExtractionStatus Status,
    string? ErrorMessage,
    DateTime Created,
    DateTime? ProcessedAt
); 