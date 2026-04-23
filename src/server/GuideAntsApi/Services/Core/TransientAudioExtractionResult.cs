using System.Threading;

namespace GuideAntsApi.Services.Core
{
    public sealed class TransientAudioExtractionResult : IAsyncDisposable
    {
        private readonly Func<ValueTask>? _disposeAsync;
        private int _disposed;

        public TransientAudioExtractionResult(
            string audioFilePath,
            long fileSize,
            string contentType,
            Func<ValueTask>? disposeAsync = null)
        {
            AudioFilePath = audioFilePath;
            FileSize = fileSize;
            ContentType = contentType;
            _disposeAsync = disposeAsync;
        }

        public string AudioFilePath { get; }
        public long FileSize { get; }
        public string ContentType { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_disposeAsync is not null)
            {
                await _disposeAsync();
            }
        }
    }
}
