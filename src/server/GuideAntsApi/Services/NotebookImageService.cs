using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Options;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;
using GuideAnts.Usage;
using AntRunner.ToolCalling.Functions;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Attributes;

namespace GuideAntsApi.Services
{
    public interface INotebookImageService
    {
        Task<ScriptExecutionResult> GenerateImageAsync(
            string prompt,
            string filename,
            string size = "1024x1024",
            int n = 1,
            string outputFormat = "png",
            InvocationContext? context = null);

        Task<ScriptExecutionResult> CreateImageFromImageAsync(
            string prompt,
            string sourceImageFilename,
            string outputFilename,
            int n = 1,
            string outputFormat = "png",
            InvocationContext? context = null);
    }

    public partial class NotebookImageService : INotebookImageService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NotebookImageService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IServiceModeResolver _serviceModeResolver;
        private const string ImageProviderCloud = ServiceProviderIds.ImageGenerationAzureOpenAiImages;
        private const string ImageProviderLocal = ServiceProviderIds.ImageGenerationLocalSdHttp;
        private const string ImageProviderGoogle = ServiceProviderIds.ImageGenerationGoogleImagen;
        private const string ImageProviderHuggingFace = ServiceProviderIds.ImageGenerationHuggingFaceInference;
        private const string ImageProviderOpenRouter = ServiceProviderIds.ImageGenerationOpenRouterImage;
        private const string ImageProviderOpenAi = ServiceProviderIds.ImageGenerationOpenAiImages;
        private const string AzureProviderSection = "AzureOpenAiImages";
        private const string LocalProviderSection = "LocalServiceHosts:ImageGenerationBaseUrl";
        private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
        private const string HuggingFaceProviderSection = "HuggingFace";
        private const string OpenRouterProviderSection = "OpenRouter";
        private const string OpenAiProviderSection = "OpenAI";
        private const string HuggingFaceRouterBaseUrl = "https://router.huggingface.co";
        private static readonly string[] CurrentImageSizes = { "1024x1024", "1024x1792", "1792x1024" };
        private static readonly string[] GptImage15Sizes = { "1024x1024", "1024x1536", "1536x1024", "auto" };
        private static readonly JsonSerializerOptions ProviderPayloadJson = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public NotebookImageService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<NotebookImageService> logger,
            IServiceProvider serviceProvider,
            IServiceModeResolver serviceModeResolver)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _serviceModeResolver = serviceModeResolver;
        }

        /// <summary>
        /// Generates an image using the configured provider and saves it to the notebook's output directory.
        /// </summary>
        public async Task<ScriptExecutionResult> GenerateImageAsync(
            string prompt,
            string filename,
            string size = "1024x1024",
            int n = 1,
            string outputFormat = "png",
            InvocationContext? context = null)
        {
            var stdOutBuffer = new StringBuilder();
            var stdErrBuffer = new StringBuilder();
            Exception? executionException = null;

            _logger.LogInformation("GenerateImage invoked. Project={ProjectId}, Notebook={NotebookId}, Prompt={Prompt}", context?.ProjectId, context?.NotebookId, prompt);

            if (string.IsNullOrWhiteSpace(prompt))
            {
                var errorMsg = "Prompt cannot be null or empty.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            if (string.IsNullOrWhiteSpace(filename))
            {
                var errorMsg = "Filename is required.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            filename = Path.GetFileName(filename);
            var expectedExtension = $".{outputFormat.ToLowerInvariant()}";
            if (!filename.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                filename += expectedExtension;
            }

            if (context?.ProjectId == null || context?.NotebookId == null)
            {
                var errorMsg = "Project ID and Notebook ID are required.";
                _logger?.LogError(errorMsg + " ProjectId='{ProjectId}', NotebookId='{NotebookId}'", context?.ProjectId, context?.NotebookId);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            var storageRoot = Environment.GetEnvironmentVariable("FileStorage__Path") ?? Environment.GetEnvironmentVariable("FILESTORAGE__PATH");
            if (string.IsNullOrWhiteSpace(storageRoot) && _serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    storageRoot = cfg["FileStorage:Path"];
                }
                catch { /* ignore — will fail below */ }
            }
            if (storageRoot == null) throw new InvalidOperationException("FileStorage:Path is not configured");
            var notebookDirectory = NotebookPathHelper.GetLocalWorkingDirectory(context!, storageRoot);

            _logger?.LogInformation("Will generate image and save to directory: {NotebookDirectory}", notebookDirectory);

            string? createdFileCwdPath = null;

            try
            {
                Directory.CreateDirectory(notebookDirectory);
                var mode = await ResolveImageGenerationModeAsync();
                var imageProvider = ResolveImageProviderId(mode);

                byte[]? imageBytes;
                var imageAttachment = _serviceProvider != null
                    ? await MediaAttachmentHelper.TryGetFirstImageAttachmentForCurrentUserMessageAsync(_serviceProvider, context!.ConversationId)
                    : null;
                if (imageAttachment != null)
                {
                    var (imageContent, contentType, sourceFileName) = imageAttachment.Value;
                    imageBytes = imageProvider switch
                    {
                        ImageProviderLocal => await GenerateImageEditViaLocalSd(
                            prompt: prompt,
                            size: size,
                            n: n,
                            outputFormat: outputFormat,
                            imageBytes: imageContent,
                            imageContentType: contentType,
                            imageFileName: sourceFileName),
                        ImageProviderCloud => await GenerateImageEditViaAzureOpenAI(
                            prompt: prompt,
                            size: size,
                            n: n,
                            imageBytes: imageContent,
                            imageContentType: contentType,
                            imageFileName: sourceFileName,
                            mode: mode),
                        ImageProviderGoogle => await GenerateImageEditViaGoogleGemini(
                            prompt: prompt,
                            size: size,
                            n: n,
                            outputFormat: outputFormat,
                            imageBytes: imageContent,
                            imageContentType: contentType,
                            imageFileName: sourceFileName,
                            modelId: mode.ModelId),
                        ImageProviderHuggingFace => await GenerateImageEditViaHuggingFace(
                            prompt: prompt,
                            size: size,
                            n: n,
                            outputFormat: outputFormat,
                            imageBytes: imageContent,
                            imageContentType: contentType,
                            imageFileName: sourceFileName,
                            modelId: mode.ModelId,
                            requestPresetJson: mode.RequestPresetJson),
                        ImageProviderOpenRouter => await GenerateImageEditViaOpenRouter(
                            prompt: prompt,
                            size: size,
                            n: n,
                            outputFormat: outputFormat,
                            imageBytes: imageContent,
                            imageContentType: contentType,
                            imageFileName: sourceFileName,
                            modelId: mode.ModelId,
                            requestPresetJson: mode.RequestPresetJson),
                        ImageProviderOpenAi => await GenerateImageEditViaOpenAi(
                            prompt: prompt,
                            size: size,
                            n: n,
                            imageBytes: imageContent,
                            imageContentType: contentType,
                            imageFileName: sourceFileName,
                            modelId: mode.ModelId),
                        _ => throw new RoutingException(
                            RoutingErrorCodes.ProviderNotReady,
                            $"Image edit provider '{imageProvider}' is not recognized.",
                            action: "Fix the service mode provider configuration.",
                            serviceId: RoutedServiceNames.ImageGeneration,
                            providerSection: imageProvider)
                    };
                }
                else
                {
                    imageBytes = imageProvider switch
                    {
                        ImageProviderLocal => await GenerateImageViaLocalSd(prompt, size, n, outputFormat),
                        ImageProviderCloud => await GenerateImageViaAzureOpenAI(prompt, size, n, outputFormat, mode),
                        ImageProviderGoogle => await GenerateImageViaGoogleGemini(prompt, size, n, outputFormat, mode.ModelId),
                        ImageProviderHuggingFace => await GenerateImageViaHuggingFace(prompt, size, n, outputFormat, mode.ModelId, mode.RequestPresetJson),
                        ImageProviderOpenRouter => await GenerateImageViaOpenRouter(prompt, size, n, outputFormat, mode.ModelId, mode.RequestPresetJson),
                        ImageProviderOpenAi => await GenerateImageViaOpenAi(prompt, size, n, mode.ModelId),
                        _ => throw new RoutingException(
                            RoutingErrorCodes.ProviderNotReady,
                            $"Image provider '{imageProvider}' is not recognized.",
                            action: "Fix the service mode provider configuration.",
                            serviceId: RoutedServiceNames.ImageGeneration,
                            providerSection: imageProvider)
                    };
                }

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    var filePath = Path.Combine(notebookDirectory, filename);
                    await File.WriteAllBytesAsync(filePath, imageBytes);

                    _logger?.LogInformation("Image saved to: {FilePath}, Size: {Size} bytes", filePath, imageBytes.Length);
                    stdOutBuffer.AppendLine($"Image generated successfully: {filename}");

                    var relativePath = Path.Combine(NotebookPathHelper.GetRelativeRunFolder(context!), filename).Replace("\\", "/");
                    createdFileCwdPath = NotebookFileChangeReporter.ToCwdRelativePath(relativePath, context.IsPublished, context.RunId);

                    if (_serviceProvider != null)
                    {
                        try
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var syncService = scope.ServiceProvider.GetRequiredService<INotebookFileSyncService>();
                            await syncService.SyncNotebookAsync(context.NotebookId);
                            _logger?.LogInformation("Database synchronized for notebook {NotebookId} after image generation", context.NotebookId);
                        }
                        catch (Exception syncEx)
                        {
                            _logger?.LogError(syncEx, "Failed to sync database after image generation");
                            stdErrBuffer.AppendLine($"Warning: Image generated but failed to sync database: {syncEx.Message}");
                        }
                    }

                    await RecordImageUsageAsync(
                        context!,
                        imageProvider,
                        filename,
                        imageBytes.Length,
                        imageCount: Math.Max(1, n),
                        operation: "image-generation");
                }
                else
                {
                    stdErrBuffer.AppendLine("Error: Image generation failed - no image data returned.");
                    _logger?.LogWarning("Image generation failed - no image data returned");
                }
            }
            catch (RoutingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error generating image: {ex.Message}";
                _logger?.LogError(ex, "Image generation failed. Project={ProjectId}, Notebook={NotebookId}, Prompt={Prompt}", context?.ProjectId, context?.NotebookId, prompt);
                stdErrBuffer.AppendLine(errorMsg);
                executionException = ex;
            }

            if (stdOutBuffer.Length == 0 && stdErrBuffer.Length == 0)
            {
                stdOutBuffer.AppendLine("The operation completed successfully");
            }

            var cleanedOutput = stdOutBuffer.ToString().Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            var cleanedError = stdErrBuffer.ToString().Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();

            var imageGenerationResult = new ScriptExecutionResult
            {
                StandardOutput = cleanedOutput,
                StandardError = cleanedError,
                NewFiles = createdFileCwdPath != null ? new List<string> { createdFileCwdPath } : null
            };

            if (!string.IsNullOrWhiteSpace(cleanedOutput))
                _logger?.LogInformation("Final StdOut (truncated): {Out}", cleanedOutput.Length > 4096 ? cleanedOutput.Substring(0, 4096) + "…" : cleanedOutput);
            if (!string.IsNullOrWhiteSpace(cleanedError))
                _logger?.LogWarning("Final StdErr (truncated): {Err}", cleanedError.Length > 4096 ? cleanedError.Substring(0, 4096) + "…" : cleanedError);

            var basePath = Environment.GetEnvironmentVariable("DOCKER_EXEC_LOG_PATH") ?? "/tmp/scripts";
            var imageFolder = Path.Combine(basePath, "GenerateImage", executionException == null ? "success" : "failure");
            Directory.CreateDirectory(imageFolder);
            var resultFilename = $"{Guid.NewGuid()}.json";
            var resultPath = Path.Combine(imageFolder, resultFilename);

            var logObject = new
            {
                Prompt = prompt,
                Size = size,
                N = n,
                OutputFormat = outputFormat,
                ProjectId = context?.ProjectId.ToString(),
                NotebookId = context?.NotebookId.ToString(),
                NotebookDirectory = notebookDirectory,
                Result = imageGenerationResult
            };

            var jsonResult = JsonSerializer.Serialize(logObject, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(resultPath, jsonResult);

            return imageGenerationResult;
        }

        public async Task<ScriptExecutionResult> CreateImageFromImageAsync(
            string prompt,
            string sourceImageFilename,
            string outputFilename,
            int n = 1,
            string outputFormat = "png",
            InvocationContext? context = null)
        {
            var stdOutBuffer = new StringBuilder();
            var stdErrBuffer = new StringBuilder();
            Exception? executionException = null;

            _logger.LogInformation("CreateImageFromImage invoked. Project={ProjectId}, Notebook={NotebookId}, SourceImage={SourceImage}, Prompt={Prompt}",
                context?.ProjectId, context?.NotebookId, sourceImageFilename, prompt);

            if (string.IsNullOrWhiteSpace(prompt))
            {
                var errorMsg = "Prompt cannot be null or empty.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
            }

            if (string.IsNullOrWhiteSpace(sourceImageFilename))
            {
                var errorMsg = "Source image filename is required.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
            }

            if (string.IsNullOrWhiteSpace(outputFilename))
            {
                var errorMsg = "Output filename is required.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
            }

            outputFilename = Path.GetFileName(outputFilename);
            var expectedExtension = $".{outputFormat.ToLowerInvariant()}";
            if (!outputFilename.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                outputFilename += expectedExtension;
            }

            if (context?.ProjectId == null || context?.NotebookId == null)
            {
                var errorMsg = "Project ID and Notebook ID are required.";
                _logger?.LogError(errorMsg + " ProjectId='{ProjectId}', NotebookId='{NotebookId}'", context?.ProjectId, context?.NotebookId);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
            }

            var storageRoot = Environment.GetEnvironmentVariable("FileStorage__Path") ?? Environment.GetEnvironmentVariable("FILESTORAGE__PATH");
            if (string.IsNullOrWhiteSpace(storageRoot) && _serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    storageRoot = cfg["FileStorage:Path"];
                }
                catch { /* ignore — will fail below */ }
            }
            if (storageRoot == null) throw new InvalidOperationException("FileStorage:Path is not configured");
            var notebookDirectory = NotebookPathHelper.GetLocalWorkingDirectory(context!, storageRoot);

            _logger?.LogInformation("Will generate image from source image and save to directory: {NotebookDirectory}", notebookDirectory);

            string? createdFileCwdPath = null;

            try
            {
                Directory.CreateDirectory(notebookDirectory);

                byte[]? sourceImageBytes = null;
                string? sourceImageContentType = null;
                string? sourceImageFileName = null;

                if (_serviceProvider != null)
                {
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var fileService = scope.ServiceProvider.GetRequiredService<INotebookFileService>();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                        var sanitizedSourceFilename = Path.GetFileName(sourceImageFilename);
                        var nbFile = await db.NotebookFiles
                            .Where(f => f.NotebookId == context.NotebookId && f.RelativePath.EndsWith(sanitizedSourceFilename))
                            .FirstOrDefaultAsync();

                        if (nbFile == null)
                        {
                            stdErrBuffer.AppendLine($"Source image file not found in notebook: {sanitizedSourceFilename}");
                            return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
                        }

                        if (!MediaAttachmentHelper.IsImageFile(nbFile.RelativePath))
                        {
                            stdErrBuffer.AppendLine($"Specified file is not a supported image format: {sanitizedSourceFilename}");
                            return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
                        }

                        var res = await fileService.GetFileContentStreamAsync(nbFile.Id);
                        if (!res.HasValue)
                        {
                            stdErrBuffer.AppendLine($"Failed to load source image file content: {sanitizedSourceFilename}");
                            return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
                        }

                        using var stream = res.Value.Stream;
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        sourceImageBytes = ms.ToArray();
                        sourceImageContentType = res.Value.ContentType;
                        sourceImageFileName = res.Value.FileName;

                        _logger?.LogInformation("Loaded source image file. Name={FileName}, Bytes={ByteCount}", sourceImageFileName, sourceImageBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to load source image file");
                        stdErrBuffer.AppendLine($"Error loading source image file: {ex.Message}");
                        return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
                    }
                }
                else
                {
                    stdErrBuffer.AppendLine("Service provider not available");
                    return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
                }

                if (sourceImageBytes == null || sourceImageBytes.Length == 0)
                {
                    stdErrBuffer.AppendLine("Failed to load source image bytes");
                    return new ScriptExecutionResult { StandardOutput = stdOutBuffer.ToString(), StandardError = stdErrBuffer.ToString() };
                }

                var mode = await ResolveImageGenerationModeAsync();
                var imageProvider = ResolveImageProviderId(mode);
                var imageSizeProfileName = ResolveImageSizeProfileName(imageProvider, mode);

                string size = DetermineBestSizeForImage(sourceImageBytes, imageSizeProfileName);
                _logger?.LogInformation("Automatically determined size '{Size}' for source image '{SourceImage}'", size, sourceImageFilename);

                byte[]? imageBytes = imageProvider switch
                {
                    ImageProviderLocal => await GenerateImageEditViaLocalSd(
                        prompt: prompt,
                        size: size,
                        n: n,
                        outputFormat: outputFormat,
                        imageBytes: sourceImageBytes,
                        imageContentType: sourceImageContentType,
                        imageFileName: sourceImageFileName),
                    ImageProviderCloud => await GenerateImageEditViaAzureOpenAI(
                        prompt: prompt,
                        size: size,
                        n: n,
                        imageBytes: sourceImageBytes,
                        imageContentType: sourceImageContentType,
                        imageFileName: sourceImageFileName,
                        mode: mode),
                    ImageProviderGoogle => await GenerateImageEditViaGoogleGemini(
                        prompt: prompt,
                        size: size,
                        n: n,
                        outputFormat: outputFormat,
                        imageBytes: sourceImageBytes,
                        imageContentType: sourceImageContentType,
                        imageFileName: sourceImageFileName,
                        modelId: mode.ModelId),
                    ImageProviderHuggingFace => await GenerateImageEditViaHuggingFace(
                        prompt: prompt,
                        size: size,
                        n: n,
                        outputFormat: outputFormat,
                        imageBytes: sourceImageBytes,
                        imageContentType: sourceImageContentType,
                        imageFileName: sourceImageFileName,
                        modelId: mode.ModelId,
                        requestPresetJson: mode.RequestPresetJson),
                    ImageProviderOpenRouter => await GenerateImageEditViaOpenRouter(
                        prompt: prompt,
                        size: size,
                        n: n,
                        outputFormat: outputFormat,
                        imageBytes: sourceImageBytes,
                        imageContentType: sourceImageContentType,
                        imageFileName: sourceImageFileName,
                        modelId: mode.ModelId,
                        requestPresetJson: mode.RequestPresetJson),
                    ImageProviderOpenAi => await GenerateImageEditViaOpenAi(
                        prompt: prompt,
                        size: size,
                        n: n,
                        imageBytes: sourceImageBytes,
                        imageContentType: sourceImageContentType,
                        imageFileName: sourceImageFileName,
                        modelId: mode.ModelId),
                    _ => throw new RoutingException(
                        RoutingErrorCodes.ProviderNotReady,
                        $"Image provider '{imageProvider}' is not recognized.",
                        action: "Fix the service mode provider configuration.",
                        serviceId: RoutedServiceNames.ImageGeneration,
                        providerSection: imageProvider)
                };

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    var filePath = Path.Combine(notebookDirectory, outputFilename);
                    await File.WriteAllBytesAsync(filePath, imageBytes);

                    _logger?.LogInformation("Image edited successfully from source '{SourceImage}': {OutputFile}, Size: {Size} bytes",
                        sourceImageFilename, outputFilename, imageBytes.Length);
                    stdOutBuffer.AppendLine($"Image edited successfully from '{sourceImageFilename}': {outputFilename}");

                    var relativePath = Path.Combine(NotebookPathHelper.GetRelativeRunFolder(context!), outputFilename).Replace("\\", "/");
                    createdFileCwdPath = NotebookFileChangeReporter.ToCwdRelativePath(relativePath, context.IsPublished, context.RunId);

                    if (_serviceProvider != null)
                    {
                        try
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var syncService = scope.ServiceProvider.GetRequiredService<INotebookFileSyncService>();
                            await syncService.SyncNotebookAsync(context.NotebookId);
                            _logger?.LogInformation("Database synchronized for notebook {NotebookId} after image edit", context.NotebookId);
                        }
                        catch (Exception syncEx)
                        {
                            _logger?.LogError(syncEx, "Failed to sync database after image edit");
                            stdErrBuffer.AppendLine($"Warning: Image generated but failed to sync database: {syncEx.Message}");
                        }
                    }

                    await RecordImageUsageAsync(
                        context!,
                        imageProvider,
                        outputFilename,
                        imageBytes.Length,
                        imageCount: Math.Max(1, n),
                        operation: "image-edit");
                }
                else
                {
                    stdErrBuffer.AppendLine("Error: Image edit failed - no image data returned.");
                    _logger?.LogWarning("Image edit failed - no image data returned");
                }
            }
            catch (RoutingException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error editing image: {ex.Message}";
                _logger?.LogError(ex, "Image edit failed. Project={ProjectId}, Notebook={NotebookId}, SourceImage={SourceImage}, Prompt={Prompt}",
                    context?.ProjectId, context?.NotebookId, sourceImageFilename, prompt);
                stdErrBuffer.AppendLine(errorMsg);
                executionException = ex;
            }

            if (stdOutBuffer.Length == 0 && stdErrBuffer.Length == 0)
            {
                stdOutBuffer.AppendLine("The operation completed successfully");
            }

            var cleanedOutput = stdOutBuffer.ToString().Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
            var cleanedError = stdErrBuffer.ToString().Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();

            var imageEditResult = new ScriptExecutionResult
            {
                StandardOutput = cleanedOutput,
                StandardError = cleanedError,
                NewFiles = createdFileCwdPath != null ? new List<string> { createdFileCwdPath } : null
            };

            if (!string.IsNullOrWhiteSpace(cleanedOutput))
                _logger?.LogInformation("Final StdOut (truncated): {Out}", cleanedOutput.Length > 4096 ? cleanedOutput.Substring(0, 4096) + "…" : cleanedOutput);
            if (!string.IsNullOrWhiteSpace(cleanedError))
                _logger?.LogWarning("Final StdErr (truncated): {Err}", cleanedError.Length > 4096 ? cleanedError.Substring(0, 4096) + "…" : cleanedError);

            var basePath = Environment.GetEnvironmentVariable("DOCKER_EXEC_LOG_PATH") ?? "/tmp/scripts";
            var imageFolder = Path.Combine(basePath, "EditImage", executionException == null ? "success" : "failure");
            Directory.CreateDirectory(imageFolder);
            var resultFilename = $"{Guid.NewGuid()}.json";
            var resultPath = Path.Combine(imageFolder, resultFilename);

            var logObject = new
            {
                Prompt = prompt,
                SourceImageFilename = sourceImageFilename,
                OutputFilename = outputFilename,
                N = n,
                OutputFormat = outputFormat,
                ProjectId = context?.ProjectId.ToString(),
                NotebookId = context?.NotebookId.ToString(),
                NotebookDirectory = notebookDirectory,
                Result = imageEditResult
            };

            var jsonResult = JsonSerializer.Serialize(logObject, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(resultPath, jsonResult);

            return imageEditResult;
        }

        private async Task RecordImageUsageAsync(
            InvocationContext context,
            string providerId,
            string filename,
            long bytes,
            int imageCount,
            string operation)
        {
            if (_serviceProvider == null)
            {
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var recorder = scope.ServiceProvider.GetRequiredService<IUsageRecorder>();
                await recorder.RecordImageAsync(
                    projectId: context.ProjectId,
                    notebookId: context.NotebookId,
                    notebookFileId: null,
                    imageCount: imageCount,
                    bytes: bytes,
                    service: providerId,
                    operation: operation,
                    conversationId: context.ConversationId,
                    metadataJson: JsonSerializer.Serialize(new
                    {
                        providerId,
                        filename,
                        imageCount,
                        bytes
                    }),
                    assistantId: context.AssistantId,
                    agentInvocationId: context.CurrentInvocationId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to record image usage. Provider={ProviderId}, Filename={Filename}",
                    providerId,
                    filename);
            }
        }

        private async Task<ServiceMode> ResolveImageGenerationModeAsync(CancellationToken cancellationToken = default)
        {
            return await _serviceModeResolver
                .ResolveAsync(RoutedServiceNames.ImageGeneration, modeId: null, cancellationToken)
                .ConfigureAwait(false);
        }

        private static string ResolveImageProviderId(ServiceMode mode)
        {
            return mode.ProviderSection switch
            {
                AzureProviderSection => ImageProviderCloud,
                LocalProviderSection => ImageProviderLocal,
                GoogleGeminiProviderSection => ImageProviderGoogle,
                HuggingFaceProviderSection => ImageProviderHuggingFace,
                OpenRouterProviderSection => ImageProviderOpenRouter,
                OpenAiProviderSection => ImageProviderOpenAi,
                _ => throw RoutingException.ProviderNotReady(
                    mode.ProviderSection,
                    new[]
                    {
                        $"ImageGeneration mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                        $"Expected '{AzureProviderSection}', '{LocalProviderSection}', '{GoogleGeminiProviderSection}', '{HuggingFaceProviderSection}', '{OpenRouterProviderSection}', or '{OpenAiProviderSection}'."
                    },
                    serviceId: RoutedServiceNames.ImageGeneration,
                    modeId: mode.ModeId)
            };
        }

        private string ResolveImageSizeProfileName(string imageProvider, ServiceMode mode)
        {
            if (imageProvider == ImageProviderLocal)
                return "flux-local";
            if (imageProvider == ImageProviderGoogle)
                return "google-imagen";
            if (imageProvider == ImageProviderHuggingFace)
                return "hf-image";
            if (imageProvider == ImageProviderOpenRouter)
                return "openrouter-image";
            if (imageProvider == ImageProviderOpenAi)
                return "openai-image";

            var editDeployment = ReadServiceModePresetField(mode.RequestPresetJson, "EditModelDeployment")
                ?? throw new InvalidOperationException(
                    "ImageGeneration Azure service mode preset must include EditModelDeployment.");
            return editDeployment;
        }

        private string ResolveLocalSdBaseUrl()
        {
            var baseUrl = _configuration[$"{LocalServiceHostsOptions.SectionName}:ImageGenerationBaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException(
                    "LocalServiceHosts:ImageGenerationBaseUrl is required for the local image generation provider.");
            }

            return baseUrl.TrimEnd('/');
        }

        private int ResolveImageGenerationTimeoutSeconds()
        {
            var configuredTimeout = _configuration[$"{ImageGenerationOptions.SectionName}:TimeoutSeconds"];
            if (int.TryParse(configuredTimeout, out var timeoutSeconds) && timeoutSeconds > 0)
            {
                return timeoutSeconds;
            }

            return 600;
        }

        private static string NormalizeLocalOutputFormat(string value)
        {
            var v = value.Trim().ToLowerInvariant();
            if (v == "jpg")
                return "jpeg";

            return v is "png" or "jpeg" or "webp" ? v : "png";
        }

        /// <summary>
        /// Uses <c>ImageGeneration:LocalOutputFormat</c> when configured; otherwise the requested format.
        /// </summary>
        private string ResolveLocalOutputFormatFromSettings(string requested)
        {
            var configured = _configuration[$"{ImageGenerationOptions.SectionName}:LocalOutputFormat"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return NormalizeLocalOutputFormat(configured);
            }

            return NormalizeLocalOutputFormat(string.IsNullOrWhiteSpace(requested) ? "png" : requested);
        }

        // Static service provider for tool calling system compatibility
        private static IServiceProvider? _staticServiceProvider;

        /// <summary>
        /// Initializes the static service provider for tool calling system compatibility.
        /// </summary>
        public static void InitializeServiceProvider(IServiceProvider serviceProvider)
        {
            _staticServiceProvider = serviceProvider;
        }

        [Tool(
            OperationId = "generate_image",
            Summary = "Generate an image using AI"
        )]
        [RequiresNotebookContext]
        public static async Task<ScriptExecutionResult> GenerateImage(
            [Parameter(Description = "The text description of the image to generate")] string prompt,
            [Parameter(Description = "The filename for the generated image")] string filename,
            [Parameter(Description = "REQUIRED Image size. Valid sizes: '1024x1024' (square, default), '1024x1792' (portrait), '1792x1024' (landscape)")] string size,
            [Parameter(Description = "Number of images to generate (only first will be saved)")] int n = 1,
            [Parameter(Description = "Output format for the image")] string outputFormat = "png",
            [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
        {
            if (_staticServiceProvider == null)
            {
                throw new InvalidOperationException("Service provider not initialized. Call InitializeServiceProvider during application startup.");
            }

            using var scope = _staticServiceProvider.CreateScope();
            var imageService = scope.ServiceProvider.GetRequiredService<INotebookImageService>();

            return await imageService.GenerateImageAsync(
                prompt,
                filename,
                size,
                n,
                outputFormat,
                context);
        }

        [Tool(
            OperationId = "MakeImageFromImage",
            Summary = "Edit or modify an existing image file in the notebook using AI. Output size is automatically determined based on source image dimensions."
        )]
        [RequiresNotebookContext]
        public static async Task<ScriptExecutionResult> CreateImageFromImage(
            [Parameter(Description = "The text description of how to modify the image")] string prompt,
            [Parameter(Description = "The filename of the source image in the notebook")] string sourceImageFilename,
            [Parameter(Description = "The filename for the output image")] string outputFilename,
            [Parameter(Description = "Number of images to generate (only first will be saved)")] int n = 1,
            [Parameter(Description = "Output format for the image")] string outputFormat = "png",
            [Parameter(Description = "Invocation context", Hidden = true)] InvocationContext? context = null)
        {
            if (_staticServiceProvider == null)
            {
                throw new InvalidOperationException("Service provider not initialized. Call InitializeServiceProvider during application startup.");
            }

            using var scope = _staticServiceProvider.CreateScope();
            var imageService = scope.ServiceProvider.GetRequiredService<INotebookImageService>();

            return await imageService.CreateImageFromImageAsync(
                prompt,
                sourceImageFilename,
                outputFilename,
                n,
                outputFormat,
                context);
        }
    }
}
