namespace GuideAntsApi.BackgroundJobs.Jobs;

/// <summary>
/// Job payload for extracting markdown from content file versions
/// </summary>
public record ExtractContentVersionMarkdownJob(Guid ContentFileVersionId);

/// <summary>
/// Job payload for extracting markdown from notebook files
/// </summary>
public record ExtractNotebookFileMarkdownJob(Guid NotebookFileId);

/// <summary>
/// Job payload for transcribing audio/video to markdown for content file versions
/// </summary>
public record TranscribeContentVersionMarkdownJob(Guid ContentFileVersionId);

/// <summary>
/// Job payload for transcribing audio/video to markdown for notebook files
/// </summary>
public record TranscribeNotebookFileMarkdownJob(Guid NotebookFileId);

/// <summary>
/// Job payload for indexing content markdown shadows
/// </summary>
public record IndexContentMarkdownShadowJob(Guid ContentFileVersionId);

/// <summary>
/// Job payload for indexing notebook markdown shadows
/// </summary>
public record IndexNotebookMarkdownShadowJob(Guid NotebookFileId);

/// <summary>
/// Job payload for syncing notebook files
/// </summary>
public record SyncNotebookJob(Guid NotebookId);

public record IndexDirectTextFileJob(Guid FileId, bool IsContentFile);

public record RetentionCleanupJob(Guid PublishedGuideId);

public record RebuildEmbeddingsJob;

