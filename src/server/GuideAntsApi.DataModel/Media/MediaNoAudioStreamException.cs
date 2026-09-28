namespace GuideAntsApi.DataModel.Media
{
    /// <summary>
    /// Raised when a media file is valid but contains no audio stream. This is a
    /// legitimate empty result (nothing to transcribe), not a processing failure:
    /// callers should treat it as a skip, not as a retryable or permanent error.
    /// </summary>
    public sealed class MediaNoAudioStreamException : InvalidOperationException
    {
        public string SourcePath { get; }

        public MediaNoAudioStreamException(string sourcePath)
            : base($"Source file contains no audio stream: {sourcePath}")
        {
            SourcePath = sourcePath;
        }
    }
}
