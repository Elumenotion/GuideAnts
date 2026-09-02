namespace GuideAntsApi.DataModel.Models;

/// <summary>
/// Describes how conversation attachment content should be interpreted.
/// Kept in the data-model assembly because it is persisted with message attachments
/// and also used by the API request/response contracts.
/// </summary>
public enum ContentUploadType
{
    ImageFile,
    ImageUrl,
    AudioFile,
    TextFile,
    SandboxFile,
    Folder
}
