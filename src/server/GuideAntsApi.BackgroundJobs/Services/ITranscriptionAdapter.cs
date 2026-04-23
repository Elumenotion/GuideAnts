namespace GuideAntsApi.BackgroundJobs.Services;

/// <summary>
/// Adapter abstraction that allows BackgroundJobs to perform audio/video transcription
/// without depending on API-layer services directly.
/// Implement this in the API project and register via DI.
/// </summary>
public interface ITranscriptionAdapter
{
    /// <summary>
    /// Returns true if the file is an audio or video type supported for transcription.
    /// </summary>
    bool IsAudioOrVideoSupported(string fileName, string contentType);

    /// <summary>
    /// Returns true if the file size is within supported limits for transcription.
    /// </summary>
    bool IsFileSizeSupported(long fileSizeBytes);

    /// <summary>
    /// Transcribes the given stream to markdown text.
    /// </summary>
    Task<string> TranscribeToMarkdownAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default);
}


