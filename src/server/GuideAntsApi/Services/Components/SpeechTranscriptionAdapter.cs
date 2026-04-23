using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.Services.Core;

namespace GuideAntsApi.Services.Components
{
    /// <summary>
    /// API-layer implementation of the BackgroundJobs transcription adapter.
    /// Delegates to ISpeechTranscriptionService and IVideoAudioExtractionService.
    /// </summary>
    public sealed class SpeechTranscriptionAdapter : ITranscriptionAdapter
    {
        private readonly ISpeechTranscriptionService _speech;

        public SpeechTranscriptionAdapter(ISpeechTranscriptionService speech)
        {
            _speech = speech;
        }

        public bool IsAudioOrVideoSupported(string fileName, string contentType)
        {
            return _speech.IsAudioFileSupported(fileName, contentType);
        }

        public bool IsFileSizeSupported(long fileSizeBytes)
        {
            return _speech.IsFileSizeSupported(fileSizeBytes);
        }

        public async Task<string> TranscribeToMarkdownAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            var result = await _speech.TranscribeAudioWithDurationAsync(content, fileName, contentType, cancellationToken);
            return string.IsNullOrWhiteSpace(result.Text) ? string.Empty : result.Text;
        }
    }
}


