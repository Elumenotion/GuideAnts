namespace GuideAntsApi.Exceptions;

/// <summary>
/// Exception thrown when an attached file's markdown extraction is not yet complete
/// and the system has exhausted its waiting period.
/// </summary>
public class AttachmentNotReadyException : Exception
{
    public Guid NotebookFileId { get; }
    public string FileName { get; }

    public AttachmentNotReadyException(Guid notebookFileId, string fileName)
        : base($"The attached file '{fileName}' is still being indexed. Please wait a moment and try again.")
    {
        NotebookFileId = notebookFileId;
        FileName = fileName;
    }

    public AttachmentNotReadyException(Guid notebookFileId, string fileName, string message)
        : base(message)
    {
        NotebookFileId = notebookFileId;
        FileName = fileName;
    }
}

