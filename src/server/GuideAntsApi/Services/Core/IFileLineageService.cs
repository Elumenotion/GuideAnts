using GuideAntsApi.DataModel.Models;

namespace GuideAntsApi.Services.Core;

/// <summary>
/// Records immutable <see cref="FileLineageEvent"/> rows for file actions.
/// </summary>
public interface IFileLineageService
{
    Task RecordAsync(
        FileKind fileKind,
        Guid projectId,
        Guid fileId,
        int? versionNumber,
        FileLineageAction action,
        Guid? notebookId,
        string storagePath,
        bool saveImmediately = true);
} 