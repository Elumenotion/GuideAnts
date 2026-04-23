namespace GuideAntsApi.Services.Core
{
    public record TranscriptionResult(string Text, long DurationSeconds);

    public interface ISpeechTranscriptionService
    {
        Task<string> TranscribeAudioAsync(Stream audioContent, string fileName, string contentType, CancellationToken cancellationToken = default);
        Task<TranscriptionResult> TranscribeAudioWithDurationAsync(Stream audioContent, string fileName, string contentType, CancellationToken cancellationToken = default);
        /// <summary>
        /// Transcribe audio with optional diarization control.
        /// </summary>
        /// <param name="audioContent">Audio stream</param>
        /// <param name="fileName">File name with extension</param>
        /// <param name="contentType">MIME content type</param>
        /// <param name="enableDiarization">If false, returns plain text without speaker labels (ideal for mic input)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task<TranscriptionResult> TranscribeAudioWithDurationAsync(Stream audioContent, string fileName, string contentType, bool enableDiarization, CancellationToken cancellationToken = default);
        bool IsAudioFileSupported(string fileName, string contentType);
        bool IsFileSizeSupported(long fileSizeBytes);
    }
} 