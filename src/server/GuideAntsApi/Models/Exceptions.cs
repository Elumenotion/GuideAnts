namespace GuideAntsApi.Models;

/// <summary>
/// Exception thrown when attempting to delete a file that is currently in use by notebooks.
/// This preserves data traceability by preventing orphaned references.
/// </summary>
public class FileInUseException : Exception
{
    public IEnumerable<FileUsageInNotebookDto> NotebooksUsingFile { get; }

    public FileInUseException(string message, IEnumerable<FileUsageInNotebookDto> notebooksUsingFile) 
        : base(message)
    {
        NotebooksUsingFile = notebooksUsingFile;
    }
}
