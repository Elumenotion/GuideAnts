using GuideAntsApi.Options;
using GuideAntsApi.Services.Core;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.Services.Components
{
    public class VideoAudioExtractionService : IVideoAudioExtractionService
    {
        private readonly VideoAudioExtractionOptions _options;
        private readonly IMediaExtractionClient _mediaExtractionClient;
        private readonly string _storageRoot;
        private readonly ILogger<VideoAudioExtractionService> _logger;
        private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        public VideoAudioExtractionService(
            IOptions<VideoAudioExtractionOptions> options,
            IMediaExtractionClient mediaExtractionClient,
            IConfiguration configuration,
            ILogger<VideoAudioExtractionService> logger)
        {
            _options = options.Value;
            _mediaExtractionClient = mediaExtractionClient;
            _storageRoot = Path.GetFullPath(configuration["FileStorage:Path"]
                ?? throw new InvalidOperationException("FileStorage:Path is not configured."));
            _logger = logger;
        }

        public async Task<TransientAudioExtractionResult> ExtractAudioToTempFileAsync(
            Stream videoContent,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(videoContent);

            var workspacePath = CreateWorkspacePath();
            var inputPath = Path.Combine(workspacePath, BuildInputFileName(fileName));
            var outputPath = Path.Combine(workspacePath, $"output.{_options.AudioFormat}");

            _logger.LogInformation(
                "Staging transient media extraction workspace {WorkspacePath} for {FileName}",
                workspacePath,
                fileName);

            try
            {
                Directory.CreateDirectory(workspacePath);
                if (videoContent.CanSeek)
                {
                    videoContent.Position = 0;
                }

                await using (var stagedVideo = new FileStream(inputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await videoContent.CopyToAsync(stagedVideo, cancellationToken);
                }

                var extractedAudio = await ExtractAudioInternalAsync(inputPath, outputPath, cancellationToken);
                return new TransientAudioExtractionResult(
                    extractedAudio.audioFilePath,
                    extractedAudio.fileSize,
                    extractedAudio.contentType,
                    () =>
                    {
                        CleanupDirectory(workspacePath);
                        return ValueTask.CompletedTask;
                    });
            }
            catch (Exception ex)
            {
                CleanupDirectory(workspacePath);
                _logger.LogError(ex, "Failed to extract audio from staged video content for {FileName}", fileName);
                throw;
            }
        }

        public async Task<(string audioFilePath, long fileSize)> ExtractAndStoreAudioAsync(
            string videoFilePath,
            string outputDirectory,
            string baseFileName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(videoFilePath) || !File.Exists(videoFilePath))
            {
                throw new FileNotFoundException($"Video file not found: {videoFilePath}");
            }

            Directory.CreateDirectory(outputDirectory);

            var audioFileName = $"{baseFileName}_audio.{_options.AudioFormat}";
            var audioFilePath = Path.Combine(outputDirectory, audioFileName);
            string? stagedWorkspace = null;
            var sourcePathForExtraction = Path.GetFullPath(videoFilePath);

            if (!IsUnderStorageRoot(sourcePathForExtraction))
            {
                stagedWorkspace = CreateWorkspacePath();
                Directory.CreateDirectory(stagedWorkspace);
                var stagedInputPath = Path.Combine(stagedWorkspace, BuildInputFileName(videoFilePath));
                File.Copy(sourcePathForExtraction, stagedInputPath, overwrite: true);
                sourcePathForExtraction = stagedInputPath;
            }

            _logger.LogInformation(
                "Extracting audio from video {VideoPath} to {AudioPath}",
                videoFilePath,
                audioFilePath);

            try
            {
                var extractedAudio = await ExtractAudioInternalAsync(sourcePathForExtraction, audioFilePath, cancellationToken);

                _logger.LogInformation(
                    "Successfully extracted audio to {AudioPath} ({FileSize} bytes)",
                    extractedAudio.audioFilePath,
                    extractedAudio.fileSize);

                return (extractedAudio.audioFilePath, extractedAudio.fileSize);
            }
            finally
            {
                CleanupDirectory(stagedWorkspace);
            }
        }

        public bool IsVideoFileSupported(string fileName, string contentType)
        {
            if (!string.IsNullOrEmpty(contentType))
            {
                var lowerContentType = contentType.ToLowerInvariant();
                var supportedContentTypes = new[]
                {
                    "video/mp4", "video/quicktime", "video/x-msvideo", "video/webm",
                    "video/x-ms-wmv", "video/x-matroska"
                };

                if (supportedContentTypes.Any(ct => lowerContentType.StartsWith(ct)))
                {
                    return true;
                }
            }

            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!string.IsNullOrEmpty(extension))
            {
                var supportedExtensions = new[]
                {
                    ".mp4", ".mov", ".avi", ".webm", ".wmv", ".mkv", ".flv", ".m4v"
                };

                return supportedExtensions.Contains(extension);
            }

            return false;
        }

        public bool IsFileSizeSupported(long fileSizeBytes)
        {
            var maxSizeBytes = _options.MaxFileSizeMB * 1024 * 1024;
            return fileSizeBytes <= maxSizeBytes;
        }

        private async Task<(string audioFilePath, long fileSize, string contentType)> ExtractAudioInternalAsync(
            string inputPath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            var normalizedInputPath = EnsurePathUnderStorageRoot(inputPath, nameof(inputPath));
            var normalizedOutputPath = EnsurePathUnderStorageRoot(outputPath, nameof(outputPath));
            Directory.CreateDirectory(Path.GetDirectoryName(normalizedOutputPath)
                ?? throw new InvalidOperationException("The output path must include a parent directory."));

            var request = new MediaExtractionRequest
            {
                SourcePath = ToStorageRelativePath(normalizedInputPath),
                OutputPath = ToStorageRelativePath(normalizedOutputPath),
                AudioFormat = _options.AudioFormat,
                AudioQuality = _options.AudioQuality,
                Overwrite = true
            };

            var response = await _mediaExtractionClient.ExtractAudioAsync(request, cancellationToken);
            if (!File.Exists(normalizedOutputPath))
            {
                throw new InvalidOperationException(
                    $"Media extraction succeeded but the output file was not found: {normalizedOutputPath}");
            }

            var fileInfo = new FileInfo(normalizedOutputPath);
            if (fileInfo.Length == 0)
            {
                File.Delete(normalizedOutputPath);
                throw new InvalidOperationException("Audio extraction failed - output file is empty");
            }

            var contentType = string.IsNullOrWhiteSpace(response.ContentType)
                ? "audio/mpeg"
                : response.ContentType;
            return (normalizedOutputPath, fileInfo.Length, contentType);
        }

        private string CreateWorkspacePath()
        {
            var workspaceId = Guid.NewGuid().ToString("N");
            return Path.Combine(_storageRoot, ".system", "media-extract", workspaceId);
        }

        private static string BuildInputFileName(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            return string.IsNullOrWhiteSpace(extension) ? "input" : $"input{extension}";
        }

        private string EnsurePathUnderStorageRoot(string path, string parameterName)
        {
            var fullPath = Path.GetFullPath(path);
            var storageRootPrefix = _storageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!fullPath.Equals(_storageRoot, _pathComparison)
                && !fullPath.StartsWith(storageRootPrefix, _pathComparison))
            {
                throw new InvalidOperationException(
                    $"{parameterName} must resolve under the shared storage root '{_storageRoot}'.");
            }

            return fullPath;
        }

        private bool IsUnderStorageRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var storageRootPrefix = _storageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.Equals(_storageRoot, _pathComparison)
                || fullPath.StartsWith(storageRootPrefix, _pathComparison);
        }

        private string ToStorageRelativePath(string absolutePath)
        {
            var relativePath = Path.GetRelativePath(_storageRoot, absolutePath);
            return relativePath.Replace('\\', '/');
        }

        private void CleanupDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up transient media workspace {WorkspacePath}", path);
            }
        }
    }
}
