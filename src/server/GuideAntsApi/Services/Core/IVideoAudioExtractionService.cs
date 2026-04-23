namespace GuideAntsApi.Services.Core
{
    public interface IVideoAudioExtractionService
    {
        /// <summary>
        /// Stages the video content into shared storage, extracts audio through the
        /// media router, and returns a disposable handle for the transient output.
        /// </summary>
        /// <param name="videoContent">Video content stream</param>
        /// <param name="fileName">Original file name (used for the staged input extension)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Transient extraction handle</returns>
        Task<TransientAudioExtractionResult> ExtractAudioToTempFileAsync(
            Stream videoContent,
            string fileName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts audio from a source file and stores it in the specified shared-storage directory.
        /// </summary>
        /// <param name="videoFilePath">Path to the video file</param>
        /// <param name="outputDirectory">Directory to store the extracted audio</param>
        /// <param name="baseFileName">Base name for the audio file (without extension)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Tuple of audio file path and file size</returns>
        Task<(string audioFilePath, long fileSize)> ExtractAndStoreAudioAsync(
            string videoFilePath,
            string outputDirectory,
            string baseFileName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if a file is a supported video format.
        /// </summary>
        bool IsVideoFileSupported(string fileName, string contentType);

        /// <summary>
        /// Checks if the file size is within supported limits.
        /// </summary>
        bool IsFileSizeSupported(long fileSizeBytes);
    }
}
