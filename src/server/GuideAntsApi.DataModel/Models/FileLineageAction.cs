namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Enumerates the different user-initiated operations that create a lineage record.
/// NOTE: These values are persisted in the database – DO NOT reorder existing items.
/// </summary>
public enum FileLineageAction
{
    Uploaded = 1,
    Versioned = 2,
    CopiedToNotebook = 3,
    PublishedToProject = 4,
    Moved = 5,
    Renamed = 6,
    Deleted = 7,
    ExternalWrite = 8,
    Created = 9
} 