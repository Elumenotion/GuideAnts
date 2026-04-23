namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Distinguishes whether a file belongs to the centrally versioned project store
/// or the filesystem-first notebook store.
/// </summary>
public enum FileKind
{
    Project = 0,
    Notebook = 1
} 