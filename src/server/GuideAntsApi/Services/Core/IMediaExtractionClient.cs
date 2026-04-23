namespace GuideAntsApi.Services.Core
{
    public interface IMediaExtractionClient
    {
        Task<MediaExtractionResponse> ExtractAudioAsync(MediaExtractionRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class MediaExtractionRequest
    {
        public string SourcePath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string AudioFormat { get; set; } = "mp3";
        public string AudioQuality { get; set; } = "2";
        public bool Overwrite { get; set; } = true;
    }

    public sealed class MediaExtractionResponse
    {
        public string OutputPath { get; set; } = string.Empty;
        public string ContentType { get; set; } = "audio/mpeg";
        public long FileSize { get; set; }
    }
}
