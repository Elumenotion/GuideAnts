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
            _ = size;
            _ = n;
            _ = outputFormat;
            var model = RequireImageModelId(
                HuggingFaceProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for HuggingFace.");
            var token = _configuration["HuggingFace:Token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("HuggingFace:Token is required.");
            }

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);

            var endpoint = $"https://api-inference.huggingface.co/models/{model}";
            using var client = _httpClientFactory.CreateClient();
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(prompt ?? string.Empty), "inputs");
            form.Add(new StringContent(
                JsonSerializer.Serialize(
                    new HuggingFaceImageEditParameters(Math.Max(1, n), 7.5),
                    ProviderPayloadJson),
                Encoding.UTF8,
                "application/json"), "parameters");
            var imagePart = new ByteArrayContent(imageBytes);
            imagePart.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            form.Add(imagePart, "image", string.IsNullOrWhiteSpace(imageFileName) ? "image.png" : imageFileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Hugging Face image edit failed: {(int)response.StatusCode} {errorBody}");
            }

            return await response.Content.ReadAsByteArrayAsync();
        }

        private async Task<byte[]?> GenerateImageEditViaOpenRouter(
            string prompt,
            string size,
            int n,
            string outputFormat,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName,
            string? modelId,
            string? requestPresetJson)
        {
            _ = outputFormat;
            var model = RequireImageModelId(
                OpenRouterProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for OpenRouter.");
            var apiKey = _configuration["OpenRouter:ApiKey"];
            var baseUrl = _configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenRouter:ApiKey is required.");
            }

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            var endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
            var dataUrl = BuildDataUrl(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType,
                imageBytes);
            var requestBody = new OpenRouterImageChatRequest(
                Model: model,
                Messages:
                [
                    new OpenRouterImageChatMessage(
                        "user",
                        [
                            new OpenRouterImageContentPart("text", prompt ?? string.Empty, null),
                            new OpenRouterImageContentPart(
                                "image_url",
                                null,
                                new OpenRouterImageUrl(dataUrl))
                        ])
                ],
                Modalities: ["image"],
                N: Math.Max(1, n),
                Size: size);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenRouter image edit failed: {(int)response.StatusCode} {result}");
            }

            return await SaveResponseAndReturnBytes(result);
        }

        private async Task<byte[]?> GenerateImageViaOpenAi(
            string prompt,
            string size,
            int n,
            string? modelId)
        {
            var apiKey = _configuration["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenAI:ApiKey is required.");
            }

            var model = RequireImageModelId(
                OpenAiProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for OpenAI.");

            var baseUrl = (_configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1").TrimEnd('/');
            var endpoint = $"{baseUrl}/images/generations";
            var requestBody = new Dictionary<string, object>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["n"] = Math.Max(1, n),
                ["size"] = size,
            };
            if (OpenAiImageModelUsesLegacyResponseFormat(model))
            {
                requestBody["response_format"] = "b64_json";
            }

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI image generation failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaOpenAi(
            string prompt,
            string size,
            int n,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName,
            string? modelId)
        {
            var apiKey = _configuration["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenAI:ApiKey is required.");
            }

            var model = RequireImageModelId(
                OpenAiProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for OpenAI.");

            var baseUrl = (_configuration["OpenAI:Endpoint"] ?? "https://api.openai.com/v1").TrimEnd('/');
            var endpoint = $"{baseUrl}/images/edits";

            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            content.Add(imageContent, "image", imageFileName ?? "image.png");
            content.Add(new StringContent(prompt), "prompt");
            content.Add(new StringContent(model), "model");
            content.Add(new StringContent(size), "size");
            content.Add(new StringContent(Math.Max(1, n).ToString()), "n");
            if (OpenAiImageModelUsesLegacyResponseFormat(model))
            {
                content.Add(new StringContent("b64_json"), "response_format");
            }

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAI image edit failed: {(int)response.StatusCode} {result}");
            }

            return await SaveResponseAndReturnBytes(result);
        }

        /// <summary>
        /// DALL·E 2/3 accept <c>response_format</c> (<c>url</c> vs <c>b64_json</c>). GPT Image models
        /// return <c>data[].b64_json</c> by default and reject this parameter.
        /// </summary>
        private static bool OpenAiImageModelUsesLegacyResponseFormat(string modelId)
        {
            var m = modelId.Trim();
            if (m.Length == 0)
            {
                return true;
            }

            return !m.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);
        }

        private static string RequireImageModelId(string providerSection, string? modelId, string action)
        {
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                return modelId;
            }

            throw new RoutingException(
                RoutingErrorCodes.ProviderNotReady,
                $"ImageGeneration mode for {providerSection} must include a model id.",
                action: action,
                serviceId: RoutedServiceNames.ImageGeneration,
                providerSection: providerSection);
        }

        private static string? ReadServiceModePresetField(string? requestPresetJson, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(requestPresetJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(requestPresetJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty(fieldName, out var node))
                {
                    return null;
                }

                return node.ValueKind == JsonValueKind.String
                    ? node.GetString()?.Trim()
                    : node.ToString().Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string GetAspectRatioForImageSize(string size) => size switch
        {
            "1024x1792" => "9:16",
            "1792x1024" => "16:9",
            _ => "1:1"
        };

        private static string GetGoogleGeminiImageSize(string size)
        {
            var parts = size.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height))
            {
                return "1K";
            }

            var maxEdge = Math.Max(width, height);
            return maxEdge switch
            {
                <= 512 => "512",
                <= 1024 => "1K",
                <= 2048 => "2K",
                _ => "4K"
            };
        }

        private static string NormalizeGoogleGeminiModelName(string modelId)
        {
            var trimmed = modelId.Trim();
            return trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"models/{trimmed}";
        }

        private static string BuildDataUrl(string contentType, byte[] bytes) =>
            $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

        private static string[] GetValidImageSizes(string deploymentName)
        {
            if (string.Equals(deploymentName, "google-imagen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deploymentName, "hf-image", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deploymentName, "openrouter-image", StringComparison.OrdinalIgnoreCase))
            {
                return CurrentImageSizes;
            }

            if (deploymentName.Contains("flux", StringComparison.OrdinalIgnoreCase))
            {
                return CurrentImageSizes;
            }

            if (string.Equals(deploymentName, "gpt-image-1.5", StringComparison.OrdinalIgnoreCase))
            {
                return GptImage15Sizes;
            }

            return CurrentImageSizes;
        }

        private async Task<byte[]?> GenerateImageViaGoogleGemini(string prompt, string size, int n, string outputFormat, string? modelId)
        {
            _ = outputFormat;
            var apiKey = _configuration["GoogleGeminiApi:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "ImageGeneration mode for GoogleGeminiApi must include a model id.",
                    action: "Set ServiceModes.ImageGeneration model id for GoogleGeminiApi.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: GoogleGeminiProviderSection);
            }
            var model = modelId;
            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{NormalizeGoogleGeminiModelName(model)}:generateContent";

            var requestBody = new GoogleGeminiGenerateContentRequest(
                Contents:
                [
                    new GoogleGeminiContent(
                        "user",
                        [
                            new GoogleGeminiPart(Text: prompt)
                        ])
                ],
                GenerationConfig: new GoogleGeminiImageGenerationConfig(
                    ResponseModalities: ["IMAGE"],
                    CandidateCount: Math.Max(1, n),
                    ImageConfig: new GoogleGeminiImageConfig(
                        GetAspectRatioForImageSize(size),
                        GetGoogleGeminiImageSize(size))));

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey);
            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google Gemini image generation failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageViaHuggingFace(
            string prompt,
            string size,
            int n,
            string outputFormat,
            string? modelId,
            string? requestPresetJson)
        {
            _ = size;
            _ = n;
            _ = outputFormat;
            var token = _configuration["HuggingFace:Token"];
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("HuggingFace:Token is required.");
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "ImageGeneration mode for HuggingFace must include a model id.",
                    action: "Set ServiceModes.ImageGeneration model id for HuggingFace.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: HuggingFaceProviderSection);
            }
            var model = modelId;
            var endpoint = $"https://api-inference.huggingface.co/models/{model}";
            var requestBody = JsonSerializer.Serialize(new HuggingFaceImageGenerationRequest(prompt), ProviderPayloadJson);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Hugging Face image generation failed: {(int)response.StatusCode} {errorBody}");
            }

            return await response.Content.ReadAsByteArrayAsync();
        }

        private async Task<byte[]?> GenerateImageViaOpenRouter(
            string prompt,
            string size,
            int n,
            string outputFormat,
            string? modelId,
            string? requestPresetJson)
        {
            _ = outputFormat;
            var apiKey = _configuration["OpenRouter:ApiKey"];
            var baseUrl = _configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("OpenRouter:ApiKey is required.");
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new RoutingException(
                    RoutingErrorCodes.ProviderNotReady,
                    "ImageGeneration mode for OpenRouter must include a model id.",
                    action: "Set ServiceModes.ImageGeneration model id for OpenRouter.",
                    serviceId: RoutedServiceNames.ImageGeneration,
                    providerSection: OpenRouterProviderSection);
            }
            var model = modelId;
            var endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
            var requestBody = new OpenRouterImageChatRequest(
                Model: model,
                Messages:
                [
                    new OpenRouterImageChatMessage(
                        "user",
                        [
                            new OpenRouterImageContentPart("text", prompt, null)
                        ])
                ],
                Modalities: ["image"],
                N: Math.Max(1, n),
                Size: size);

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody, ProviderPayloadJson), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenRouter image generation failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }



        /// <summary>
        /// Saves response and returns the first image bytes (based on provided implementation)
        /// </summary>
        private Task<byte[]?> SaveResponseAndReturnBytes(string responseJson)
        {
            try
            {
                var json = JsonDocument.Parse(responseJson);

                // Handle error payloads explicitly to avoid obscuring the root cause
                if (json.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var code = errorElement.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : null;
                    var message = errorElement.TryGetProperty("message", out var msgEl) ? msgEl.ToString() : null;
                    var status = errorElement.TryGetProperty("status", out var statusEl) ? statusEl.ToString() : null;
                    var composed = string.Join(" ", new[] { status, code }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    var finalMessage = string.IsNullOrWhiteSpace(composed) ? (message ?? "Unknown error") : ($"{composed}: {message}");
                    throw new InvalidOperationException(finalMessage);
                }

                if (json.RootElement.TryGetProperty("data", out var data))
                {
                    if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                    {
                        var firstImage = data[0];
                        if (firstImage.TryGetProperty("b64_json", out var b64Property))
                        {
                            var b64 = b64Property.GetString();
                            if (!string.IsNullOrEmpty(b64))
                            {
                                var bytes = Convert.FromBase64String(b64);
                                _logger.LogInformation("Image generated successfully, size: {Size} bytes", bytes.Length);
                                return Task.FromResult<byte[]?>(bytes);
                            }
                        }
                    }
                }

                if (TryExtractOpenRouterChatImageBytes(json.RootElement, out var openRouterBytes))
                {
                    return Task.FromResult<byte[]?>(openRouterBytes);
                }

                if (TryExtractGoogleGeminiImageBytes(json.RootElement, out var googleGeminiBytes))
                {
                    return Task.FromResult<byte[]?>(googleGeminiBytes);
                }

                if (json.RootElement.TryGetProperty("predictions", out var predictions) &&
                    predictions.ValueKind == JsonValueKind.Array &&
                    predictions.GetArrayLength() > 0)
                {
                    var first = predictions[0];
                    if (first.TryGetProperty("bytesBase64Encoded", out var bytesEl))
                    {
                        var b64 = bytesEl.GetString();
                        if (!string.IsNullOrWhiteSpace(b64))
                        {
                            return Task.FromResult<byte[]?>(Convert.FromBase64String(b64));
                        }
                    }
                }

                _logger.LogError("Image generation response did not contain data. Raw response: {Response}", responseJson);
                return Task.FromResult<byte[]?>(null);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to parse image generation response: {Message}. Raw response: {Response}", ex.Message, responseJson);
                return Task.FromResult<byte[]?>(null);
            }
        }

        private static string ExtractErrorMessage(string responseJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : null;
                    var message = error.TryGetProperty("message", out var msgEl) ? msgEl.ToString() : null;
                    var status = error.TryGetProperty("status", out var statusEl) ? statusEl.ToString() : null;
                    var composed = string.Join(" ", new[] { status, code }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    return string.IsNullOrWhiteSpace(composed) ? (message ?? "") : ($"{composed}: {message}");
                }
                if (root.TryGetProperty("message", out var msg))
                {
                    return msg.ToString();
                }
            }
            catch
            {
                // ignore parse errors; we will fall back to raw json
            }
            return responseJson;
        }

        private static bool TryExtractGoogleGeminiImageBytes(JsonElement root, out byte[]? bytes)
        {
            bytes = null;
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return false;
            }

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var inlineData)
                        || inlineData.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!inlineData.TryGetProperty("data", out var dataElement))
                    {
                        continue;
                    }

                    var base64 = dataElement.GetString();
                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        bytes = Convert.FromBase64String(base64);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractOpenRouterChatImageBytes(JsonElement root, out byte[]? bytes)
        {
            bytes = null;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return false;
            }

            var message = choices[0].TryGetProperty("message", out var messageElement)
                ? messageElement
                : default;

            if (message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("images", out var images)
                    && TryExtractImageBytesFromImageCollection(images, out bytes))
                {
                    return true;
                }

                if (message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (part.TryGetProperty("image_url", out var imageUrl)
                            && TryExtractImageBytesFromImageUrl(imageUrl, out bytes))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryExtractImageBytesFromImageCollection(JsonElement images, out byte[]? bytes)
        {
            bytes = null;
            if (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0)
            {
                return false;
            }

            var first = images[0];
            if (first.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (first.TryGetProperty("image_url", out var imageUrl))
            {
                return TryExtractImageBytesFromImageUrl(imageUrl, out bytes);
            }

            if (first.TryGetProperty("b64_json", out var b64Property))
            {
                var base64 = b64Property.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    bytes = Convert.FromBase64String(base64);
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractImageBytesFromImageUrl(JsonElement imageUrl, out byte[]? bytes)
        {
            bytes = null;
            var url = imageUrl.ValueKind == JsonValueKind.Object && imageUrl.TryGetProperty("url", out var urlProp)
                ? urlProp.GetString()
                : imageUrl.GetString();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var commaIndex = url.IndexOf(',');
            if (commaIndex < 0)
            {
                return false;
            }

            bytes = Convert.FromBase64String(url[(commaIndex + 1)..]);
            return true;
        }

        private sealed record GoogleGeminiGenerateContentRequest(
            IReadOnlyList<GoogleGeminiContent> Contents,
            GoogleGeminiImageGenerationConfig GenerationConfig);

        private sealed record GoogleGeminiImageGenerationConfig(
            IReadOnlyList<string> ResponseModalities,
            int CandidateCount,
            GoogleGeminiImageConfig ImageConfig);

        private sealed record GoogleGeminiImageConfig(
            string AspectRatio,
            string ImageSize);

        private sealed record GoogleGeminiContent(
            string Role,
            IReadOnlyList<GoogleGeminiPart> Parts);

        private sealed record GoogleGeminiPart(
            string? Text = null,
            GoogleGeminiBlob? InlineData = null);

        private sealed record GoogleGeminiBlob(string MimeType, string Data);

        private sealed record HuggingFaceImageGenerationRequest(string Inputs);

        private sealed record HuggingFaceImageEditParameters(
            [property: JsonPropertyName("num_images")] int NumImages,
            [property: JsonPropertyName("guidance_scale")] double GuidanceScale);

        private sealed record OpenRouterImageChatRequest(
            string Model,
            IReadOnlyList<OpenRouterImageChatMessage> Messages,
            IReadOnlyList<string> Modalities,
            int N,
            string Size);

        private sealed record OpenRouterImageChatMessage(
            string Role,
            IReadOnlyList<OpenRouterImageContentPart> Content);

        private sealed record OpenRouterImageContentPart(
            string Type,
            string? Text,
            [property: JsonPropertyName("image_url")] OpenRouterImageUrl? ImageUrl);

        private sealed record OpenRouterImageUrl(string Url);


        // Static service provider for tool calling system compatibility
        private static IServiceProvider? _staticServiceProvider;

        /// <summary>
        /// Initializes the static service provider for tool calling system compatibility
        /// </summary>
        public static void InitializeServiceProvider(IServiceProvider serviceProvider)
        {
            _staticServiceProvider = serviceProvider;
        }

        /// <summary>
        /// Static wrapper method for tool calling system compatibility
        /// </summary>
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
