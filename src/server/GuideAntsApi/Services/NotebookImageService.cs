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

    public class NotebookImageService : INotebookImageService
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
        private const string AzureProviderSection = "AzureOpenAiImages";
        private const string LocalProviderSection = "LocalServiceHosts:ImageGenerationBaseUrl";
        private const string GoogleGeminiProviderSection = "GoogleGeminiApi";
        private const string HuggingFaceProviderSection = "HuggingFace";
        private const string OpenRouterProviderSection = "OpenRouter";
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
        /// Generates an image using Azure OpenAI DALL-E and saves it to the notebook's output directory.
        /// This method is called by the tool calling system with injected notebook context.
        /// </summary>
        /// <param name="prompt">The text description of the image to generate</param>
        /// <param name="size">Image size (e.g., '1024x1024')</param>
        /// <param name="n">Number of images to generate (only first will be saved)</param>
        /// <param name="filename">The filename for the generated image</param>
        /// <param name="outputFormat">Output format for the image</param>
        /// <param name="context">The invocation context containing project, notebook, user, and conversation IDs</param>
        /// <returns>Image generation result</returns>
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


            // Validate filename
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

            // Sanitize filename to prevent path traversal and ensure extension matches outputFormat
            filename = Path.GetFileName(filename);
            var expectedExtension = $".{outputFormat.ToLowerInvariant()}";
            if (!filename.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                filename += expectedExtension;
            }

            // Validate project and notebook IDs
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

            // Determine notebook output directory based on FileStorage path configuration
            var storageRoot = Environment.GetEnvironmentVariable("FileStorage__Path") ?? Environment.GetEnvironmentVariable("FILESTORAGE__PATH");
            if (string.IsNullOrWhiteSpace(storageRoot) && _serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    storageRoot = cfg["FileStorage:Path"];
                }
                catch { /* ignore � will fall back to default */ }
            }
            if (storageRoot == null) throw new InvalidOperationException("FileStorage:Path is not configured");
            var notebookDirectory = NotebookPathHelper.GetLocalWorkingDirectory(context!, storageRoot);

            _logger?.LogInformation("Will generate image and save to directory: {NotebookDirectory}", notebookDirectory);

            // Track the created file for NewFiles property
            string? createdFileCwdPath = null;

            try
            {
                // Ensure output directory exists
                Directory.CreateDirectory(notebookDirectory);
                var mode = await ResolveImageGenerationModeAsync();
                var imageProvider = ResolveImageProviderId(mode);

                // If the current user message has an image attachment, use the Edits endpoint with EditModelDeployment
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
                    // Otherwise, use the Generations endpoint with the standard Deployment
                    imageBytes = imageProvider switch
                    {
                        ImageProviderLocal => await GenerateImageViaLocalSd(prompt, size, n, outputFormat),
                        ImageProviderCloud => await GenerateImageViaAzureOpenAI(prompt, size, n, outputFormat, mode),
                        ImageProviderGoogle => await GenerateImageViaGoogleGemini(prompt, size, n, outputFormat, mode.ModelId),
                        ImageProviderHuggingFace => await GenerateImageViaHuggingFace(prompt, size, n, outputFormat, mode.ModelId, mode.RequestPresetJson),
                        ImageProviderOpenRouter => await GenerateImageViaOpenRouter(prompt, size, n, outputFormat, mode.ModelId, mode.RequestPresetJson),
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
                    // Build full file path
                    var filePath = Path.Combine(notebookDirectory, filename);

                    // Save image to file
                    await File.WriteAllBytesAsync(filePath, imageBytes);

                    _logger?.LogInformation("Image saved to: {FilePath}, Size: {Size} bytes", filePath, imageBytes.Length);
                    // Image generation usage is recorded in ConversationService finalization
                    stdOutBuffer.AppendLine($"Image generated successfully: {filename}");

                    // Calculate relative path for the generated file
                    var relativePath = Path.Combine(NotebookPathHelper.GetRelativeRunFolder(context!), filename).Replace("\\", "/");

                    // Convert to CWD-relative path for the assistant
                    createdFileCwdPath = NotebookFileChangeReporter.ToCwdRelativePath(relativePath, context.IsPublished, context.RunId);

                    // Reconcile DB with disk before returning so /files/content can resolve the new file.
                    // Markdown extraction and indexing are enqueued inside sync (not awaited).
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

            // Clean up output formatting
            var cleanedOutput = stdOutBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd();

            var cleanedError = stdErrBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd();

            var imageGenerationResult = new ScriptExecutionResult
            {
                StandardOutput = cleanedOutput,
                StandardError = cleanedError,
                NewFiles = createdFileCwdPath != null ? new List<string> { createdFileCwdPath } : null
            };

            // Emit final buffers to logs (truncated to 4k chars)
            if (!string.IsNullOrWhiteSpace(cleanedOutput))
            {
                _logger?.LogInformation("Final StdOut (truncated): {Out}", cleanedOutput.Length > 4096 ? cleanedOutput.Substring(0, 4096) + "…" : cleanedOutput);
            }
            if (!string.IsNullOrWhiteSpace(cleanedError))
            {
                _logger?.LogWarning("Final StdErr (truncated): {Err}", cleanedError.Length > 4096 ? cleanedError.Substring(0, 4096) + "…" : cleanedError);
            }

            // Log the execution result
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
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            if (string.IsNullOrWhiteSpace(sourceImageFilename))
            {
                var errorMsg = "Source image filename is required.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            // Validate output filename
            if (string.IsNullOrWhiteSpace(outputFilename))
            {
                var errorMsg = "Output filename is required.";
                _logger?.LogError(errorMsg);
                stdErrBuffer.AppendLine(errorMsg);
                return new ScriptExecutionResult
                {
                    StandardOutput = stdOutBuffer.ToString(),
                    StandardError = stdErrBuffer.ToString(),
                };
            }

            // Sanitize filename to prevent path traversal and ensure extension matches outputFormat
            outputFilename = Path.GetFileName(outputFilename);
            var expectedExtension = $".{outputFormat.ToLowerInvariant()}";
            if (!outputFilename.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                outputFilename += expectedExtension;
            }

            // Validate project and notebook IDs
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

            // Determine notebook output directory based on FileStorage path configuration
            var storageRoot = Environment.GetEnvironmentVariable("FileStorage__Path") ?? Environment.GetEnvironmentVariable("FILESTORAGE__PATH");
            if (string.IsNullOrWhiteSpace(storageRoot) && _serviceProvider != null)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    storageRoot = cfg["FileStorage:Path"];
                }
                catch { /* ignore � will fall back to default */ }
            }
            if (storageRoot == null) throw new InvalidOperationException("FileStorage:Path is not configured");
            var notebookDirectory = NotebookPathHelper.GetLocalWorkingDirectory(context!, storageRoot);

            _logger?.LogInformation("Will generate image from source image and save to directory: {NotebookDirectory}", notebookDirectory);

            // Track the created file for NewFiles property
            string? createdFileCwdPath = null;

            try
            {
                // Ensure output directory exists
                Directory.CreateDirectory(notebookDirectory);

                // Load the specified source image file from the notebook
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

                        // Find the notebook file by relative path
                        var sanitizedSourceFilename = Path.GetFileName(sourceImageFilename);
                        var nbFile = await db.NotebookFiles
                            .Where(f => f.NotebookId == context.NotebookId && f.RelativePath.EndsWith(sanitizedSourceFilename))
                            .FirstOrDefaultAsync();

                        if (nbFile == null)
                        {
                            stdErrBuffer.AppendLine($"Source image file not found in notebook: {sanitizedSourceFilename}");
                            return new ScriptExecutionResult
                            {
                                StandardOutput = stdOutBuffer.ToString(),
                                StandardError = stdErrBuffer.ToString(),
                            };
                        }

                        if (!MediaAttachmentHelper.IsImageFile(nbFile.RelativePath))
                        {
                            stdErrBuffer.AppendLine($"Specified file is not a supported image format: {sanitizedSourceFilename}");
                            return new ScriptExecutionResult
                            {
                                StandardOutput = stdOutBuffer.ToString(),
                                StandardError = stdErrBuffer.ToString(),
                            };
                        }

                        var res = await fileService.GetFileContentStreamAsync(nbFile.Id);
                        if (!res.HasValue)
                        {
                            stdErrBuffer.AppendLine($"Failed to load source image file content: {sanitizedSourceFilename}");
                            return new ScriptExecutionResult
                            {
                                StandardOutput = stdOutBuffer.ToString(),
                                StandardError = stdErrBuffer.ToString(),
                            };
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
                        return new ScriptExecutionResult
                        {
                            StandardOutput = stdOutBuffer.ToString(),
                            StandardError = stdErrBuffer.ToString(),
                        };
                    }
                }
                else
                {
                    stdErrBuffer.AppendLine("Service provider not available");
                    return new ScriptExecutionResult
                    {
                        StandardOutput = stdOutBuffer.ToString(),
                        StandardError = stdErrBuffer.ToString(),
                    };
                }

                if (sourceImageBytes == null || sourceImageBytes.Length == 0)
                {
                    stdErrBuffer.AppendLine("Failed to load source image bytes");
                    return new ScriptExecutionResult
                    {
                        StandardOutput = stdOutBuffer.ToString(),
                        StandardError = stdErrBuffer.ToString(),
                    };
                }

                var mode = await ResolveImageGenerationModeAsync();
                var imageProvider = ResolveImageProviderId(mode);
                var imageSizeProfileName = ResolveImageSizeProfileName(imageProvider, mode);

                // Determine the best size based on source image dimensions
                string size = DetermineBestSizeForImage(sourceImageBytes, imageSizeProfileName);
                _logger?.LogInformation("Automatically determined size '{Size}' for source image '{SourceImage}'", size, sourceImageFilename);

                // Generate edited image using the loaded source image
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
                    _ => throw new RoutingException(
                        RoutingErrorCodes.ProviderNotReady,
                        $"Image provider '{imageProvider}' is not recognized.",
                        action: "Fix the service mode provider configuration.",
                        serviceId: RoutedServiceNames.ImageGeneration,
                        providerSection: imageProvider)
                };

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    // Build full file path
                    var filePath = Path.Combine(notebookDirectory, outputFilename);

                    // Save image to file
                    await File.WriteAllBytesAsync(filePath, imageBytes);

                    _logger?.LogInformation("Image edited successfully from source '{SourceImage}': {OutputFile}, Size: {Size} bytes",
                        sourceImageFilename, outputFilename, imageBytes.Length);
                    // Image generation usage is recorded in ConversationService finalization
                    stdOutBuffer.AppendLine($"Image edited successfully from '{sourceImageFilename}': {outputFilename}");

                    // Calculate relative path for the generated file
                    var relativePath = Path.Combine(NotebookPathHelper.GetRelativeRunFolder(context!), outputFilename).Replace("\\", "/");

                    // Convert to CWD-relative path for the assistant
                    createdFileCwdPath = NotebookFileChangeReporter.ToCwdRelativePath(relativePath, context.IsPublished, context.RunId);

                    // Reconcile DB with disk before returning so /files/content can resolve the new file.
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

            // Clean up output formatting
            var cleanedOutput = stdOutBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd();

            var cleanedError = stdErrBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .TrimEnd();

            var imageEditResult = new ScriptExecutionResult
            {
                StandardOutput = cleanedOutput,
                StandardError = cleanedError,
                NewFiles = createdFileCwdPath != null ? new List<string> { createdFileCwdPath } : null
            };

            // Emit final buffers to logs (truncated to 4k chars)
            if (!string.IsNullOrWhiteSpace(cleanedOutput))
            {
                _logger?.LogInformation("Final StdOut (truncated): {Out}", cleanedOutput.Length > 4096 ? cleanedOutput.Substring(0, 4096) + "…" : cleanedOutput);
            }
            if (!string.IsNullOrWhiteSpace(cleanedError))
            {
                _logger?.LogWarning("Final StdErr (truncated): {Err}", cleanedError.Length > 4096 ? cleanedError.Substring(0, 4096) + "…" : cleanedError);
            }

            // Log the execution result
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
                _ => throw RoutingException.ProviderNotReady(
                    mode.ProviderSection,
                    new[]
                    {
                        $"ImageGeneration mode '{mode.ModeId}' references unsupported provider section '{mode.ProviderSection}'. " +
                        $"Expected '{AzureProviderSection}', '{LocalProviderSection}', '{GoogleGeminiProviderSection}', '{HuggingFaceProviderSection}', or '{OpenRouterProviderSection}'."
                    },
                    serviceId: RoutedServiceNames.ImageGeneration,
                    modeId: mode.ModeId)
            };
        }

        private string ResolveImageSizeProfileName(string imageProvider, ServiceMode mode)
        {
            if (imageProvider == ImageProviderLocal)
            {
                return "flux-local";
            }

            if (imageProvider == ImageProviderGoogle)
            {
                return "google-imagen";
            }

            if (imageProvider == ImageProviderHuggingFace)
            {
                return "hf-image";
            }

            if (imageProvider == ImageProviderOpenRouter)
            {
                return "openrouter-image";
            }

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
            {
                return "jpeg";
            }

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

        private async Task<byte[]?> GenerateImageViaLocalSd(string prompt, string size, int n, string outputFormat)
        {
            outputFormat = ResolveLocalOutputFormatFromSettings(outputFormat);
            ValidateLocalSdSize(size);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(ResolveImageGenerationTimeoutSeconds());

            var endpoint = $"{ResolveLocalSdBaseUrl()}/sd/txt2img";
            var requestBody = new
            {
                prompt,
                size,
                n,
                outputFormat
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            var requestId = AttachLocalSdCorrelationHeaders(request);
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Calling local SD txt2img. Endpoint={Endpoint}, RequestId={RequestId}, Size={Size}, N={N}, OutputFormat={OutputFormat}",
                endpoint,
                requestId,
                size,
                n,
                outputFormat);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? string.Empty;
                var error = ExtractErrorMessage(responseBody);
                _logger.LogWarning(
                    "Local SD txt2img API error. RequestId={RequestId}, StatusCode={StatusCode}, Reason={Reason}, LatencyMs={LatencyMs}, Error={ApiError}",
                    requestId,
                    statusCode,
                    reason,
                    stopwatch.ElapsedMilliseconds,
                    error);
                throw new InvalidOperationException($"Local SD txt2img API error {statusCode} {reason}: {error}");
            }

            _logger.LogInformation(
                "Local SD txt2img succeeded. RequestId={RequestId}, StatusCode={StatusCode}, LatencyMs={LatencyMs}, ResponseBytes={ResponseBytes}",
                requestId,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                responseBody.Length);
            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaLocalSd(
            string prompt,
            string size,
            int n,
            string outputFormat,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName)
        {
            outputFormat = ResolveLocalOutputFormatFromSettings(outputFormat);
            ValidateLocalSdSize(size);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(ResolveImageGenerationTimeoutSeconds());

            var endpoint = $"{ResolveLocalSdBaseUrl()}/sd/img2img";

            // Keep behavior consistent with cloud edit path; large source images are resized before upload.
            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(size), "size");
            form.Add(new StringContent(prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(n.ToString()), "n");
            form.Add(new StringContent(outputFormat ?? "png"), "outputFormat");

            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            form.Add(imageContent, "image", string.IsNullOrWhiteSpace(imageFileName) ? "image.png" : imageFileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = form
            };
            var requestId = AttachLocalSdCorrelationHeaders(request);
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation(
                "Calling local SD img2img. Endpoint={Endpoint}, RequestId={RequestId}, Size={Size}, N={N}, OutputFormat={OutputFormat}, InputBytes={InputBytes}, InputContentType={InputContentType}, InputFileName={InputFileName}",
                endpoint,
                requestId,
                size,
                n,
                outputFormat,
                imageBytes.Length,
                imageContentType,
                imageFileName);

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? string.Empty;
                var error = ExtractErrorMessage(responseBody);
                _logger.LogWarning(
                    "Local SD img2img API error. RequestId={RequestId}, StatusCode={StatusCode}, Reason={Reason}, LatencyMs={LatencyMs}, Error={ApiError}",
                    requestId,
                    statusCode,
                    reason,
                    stopwatch.ElapsedMilliseconds,
                    error);
                throw new InvalidOperationException($"Local SD img2img API error {statusCode} {reason}: {error}");
            }

            _logger.LogInformation(
                "Local SD img2img succeeded. RequestId={RequestId}, StatusCode={StatusCode}, LatencyMs={LatencyMs}, ResponseBytes={ResponseBytes}",
                requestId,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                responseBody.Length);
            return await SaveResponseAndReturnBytes(responseBody);
        }

        private static string AttachLocalSdCorrelationHeaders(HttpRequestMessage request)
        {
            var activity = Activity.Current;
            var requestId = activity?.TraceId.ToString();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                requestId = Guid.NewGuid().ToString("N");
            }

            request.Headers.TryAddWithoutValidation("x-request-id", requestId);

            if (!string.IsNullOrWhiteSpace(activity?.Id))
            {
                request.Headers.TryAddWithoutValidation("traceparent", activity.Id);
            }

            if (!string.IsNullOrWhiteSpace(activity?.TraceStateString))
            {
                request.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
            }

            return requestId;
        }

        private static void ValidateLocalSdSize(string size)
        {
            if (!CurrentImageSizes.Contains(size))
            {
                throw new ArgumentException(
                    $"Invalid size '{size}'. Valid sizes are: {string.Join(", ", CurrentImageSizes)}",
                    nameof(size));
            }
        }

        /// <summary>
        /// Determines the best output size based on the source image dimensions.
        /// // Returns the closest matching size from: 1024x1024 (square), 1024x1792 (portrait), 1792x1024 (landscape)
        /// Returns the closest matching size for gpt-image: 1024x1024 (square), 1024x1536 (portrait), 1536x1024 (landscape)
        /// </summary>
        private string DetermineBestSizeForImage(byte[] imageBytes, string deploymentName)
        {
            try
            {
                var (width, height) = GetImageDimensions(imageBytes);

                if (width <= 0 || height <= 0)
                {
                    _logger.LogWarning("Unable to determine image dimensions, defaulting to 1024x1024");
                    return "1024x1024";
                }

                var useCurrentSizes = deploymentName.Contains("flux", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(deploymentName, "gpt-image-1.5", StringComparison.OrdinalIgnoreCase);

                double aspectRatio = (double)width / height;
                _logger.LogInformation("Source image dimensions: {Width}x{Height}, aspect ratio: {AspectRatio:F2}", width, height, aspectRatio);

                // Calculate aspect ratios for our available sizes
                const double squareRatio = 1.0;        // 1024/1024 = 1.0
                // const double portraitRatio = 0.571;    // 1024/1792 = 0.571
                // const double landscapeRatio = 1.75;    // 1792/1024 = 1.75
                double portraitRatio = useCurrentSizes ? (1024.0 / 1792.0) : (1024.0 / 1536.0);
                double landscapeRatio = useCurrentSizes ? (1792.0 / 1024.0) : (1536.0 / 1024.0);

                // Find the closest match
                double squareDiff = Math.Abs(aspectRatio - squareRatio);
                double portraitDiff = Math.Abs(aspectRatio - portraitRatio);
                double landscapeDiff = Math.Abs(aspectRatio - landscapeRatio);

                if (portraitDiff < squareDiff && portraitDiff < landscapeDiff)
                {
                    var portraitSize = useCurrentSizes ? "1024x1792" : "1024x1536";
                    // _logger.LogInformation("Selected portrait orientation (1024x1792)");
                    _logger.LogInformation("Selected portrait orientation ({Size})", portraitSize);
                    return portraitSize;
                }
                else if (landscapeDiff < squareDiff && landscapeDiff < portraitDiff)
                {
                    var landscapeSize = useCurrentSizes ? "1792x1024" : "1536x1024";
                    // _logger.LogInformation("Selected landscape orientation (1792x1024)");
                    _logger.LogInformation("Selected landscape orientation ({Size})", landscapeSize);
                    return landscapeSize;
                }
                else
                {
                    _logger.LogInformation("Selected square orientation (1024x1024)");
                    return "1024x1024";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to determine image dimensions, defaulting to 1024x1024");
                return "1024x1024";
            }
        }

        /// <summary>
        /// Extracts image dimensions from common image format headers (PNG, JPEG, GIF, BMP, WEBP)
        /// </summary>
        private static (int width, int height) GetImageDimensions(byte[] imageBytes)
        {
            if (imageBytes == null || imageBytes.Length < 24)
                return (0, 0);

            // PNG: Starts with 89 50 4E 47, dimensions at offset 16-23
            if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
            {
                int width = (imageBytes[16] << 24) | (imageBytes[17] << 16) | (imageBytes[18] << 8) | imageBytes[19];
                int height = (imageBytes[20] << 24) | (imageBytes[21] << 16) | (imageBytes[22] << 8) | imageBytes[23];
                return (width, height);
            }

            // JPEG: Starts with FF D8
            if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
            {
                int pos = 2;
                while (pos < imageBytes.Length - 9)
                {
                    if (imageBytes[pos] != 0xFF)
                        break;

                    byte marker = imageBytes[pos + 1];
                    // SOF0, SOF1, SOF2 markers contain dimensions
                    if (marker >= 0xC0 && marker <= 0xC3)
                    {
                        int height = (imageBytes[pos + 5] << 8) | imageBytes[pos + 6];
                        int width = (imageBytes[pos + 7] << 8) | imageBytes[pos + 8];
                        return (width, height);
                    }

                    // Skip to next marker
                    int segmentLength = (imageBytes[pos + 2] << 8) | imageBytes[pos + 3];
                    pos += 2 + segmentLength;
                }
                return (0, 0);
            }

            // GIF: Starts with 47 49 46 (GIF), dimensions at offset 6-9
            if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46)
            {
                int width = imageBytes[6] | (imageBytes[7] << 8);
                int height = imageBytes[8] | (imageBytes[9] << 8);
                return (width, height);
            }

            // BMP: Starts with 42 4D (BM), dimensions at offset 18-25
            if (imageBytes[0] == 0x42 && imageBytes[1] == 0x4D && imageBytes.Length >= 26)
            {
                int width = imageBytes[18] | (imageBytes[19] << 8) | (imageBytes[20] << 16) | (imageBytes[21] << 24);
                int height = imageBytes[22] | (imageBytes[23] << 8) | (imageBytes[24] << 16) | (imageBytes[25] << 24);
                return (width, Math.Abs(height)); // Height can be negative for bottom-up BMPs
            }

            // WEBP: Starts with RIFF and contains WEBP
            if (imageBytes.Length >= 30 &&
                imageBytes[0] == 0x52 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46 && imageBytes[3] == 0x46 &&
                imageBytes[8] == 0x57 && imageBytes[9] == 0x45 && imageBytes[10] == 0x42 && imageBytes[11] == 0x50)
            {
                // VP8 lossless
                if (imageBytes[12] == 0x56 && imageBytes[13] == 0x50 && imageBytes[14] == 0x38 && imageBytes[15] == 0x4C)
                {
                    int width = (((imageBytes[21] & 0x3F) << 8) | imageBytes[20]) + 1;
                    int height = (((imageBytes[23] & 0xF) << 10) | (imageBytes[22] << 2) | ((imageBytes[21] & 0xC0) >> 6)) + 1;
                    return (width, height);
                }
                // VP8 lossy
                if (imageBytes.Length >= 32 && imageBytes[12] == 0x56 && imageBytes[13] == 0x50 && imageBytes[14] == 0x38 && imageBytes[15] == 0x20)
                {
                    int width = ((imageBytes[26] << 8) | imageBytes[27]) & 0x3FFF;
                    int height = ((imageBytes[28] << 8) | imageBytes[29]) & 0x3FFF;
                    return (width, height);
                }
            }

            return (0, 0);
        }

        /// <summary>
        /// Generates an image via Azure OpenAI DALL-E API using the provided implementation pattern
        /// </summary>
        private async Task<byte[]?> GenerateImageViaAzureOpenAI(string prompt, string size, int n, string outputFormat, ServiceMode mode)
        {
            try
            {
                // You will need to set these environment variables or edit the following values.
                var endpoint = _configuration["AzureOpenAiImages:Endpoint"] ?? throw new Exception("Bad config");
                var deployment = !string.IsNullOrWhiteSpace(mode.ModelId)
                    ? mode.ModelId.Trim()
                    : throw new InvalidOperationException("ImageGeneration Azure service mode must include Deployment/ModelId.");
                var apiVersion = _configuration["AzureOpenAiImages:ApiVersion"] ?? throw new Exception("Bad config:ApiVersion");
                var apiKey = _configuration["AzureOpenAiImages:ApiKey"];

                // Validate size parameter - only accept specific dimensions
                var validSizes = GetValidImageSizes(deployment);
                if (!validSizes.Contains(size))
                {
                    throw new ArgumentException($"Invalid size '{size}'. Valid sizes are: {string.Join(", ", validSizes)}", nameof(size));
                }

                var basePath = $"openai/deployments/{deployment}/images";
                var urlParams = $"?api-version={apiVersion}";

                //https://guideants-ai-images.services.ai.azure.com/openai/deployments/FLUX.1-Kontext-pro/images/generations?api-version=2025-04-01-preview

                using var client = _httpClientFactory.CreateClient();

                var generationUrl = $"{endpoint}{basePath}/generations{urlParams}";

                object generationBody = new
                {
                    prompt = prompt,
                    n = n,
                    size = size,
                    model = deployment,
                };
                //{ "size":"1024x1024","prompt":"Futuristic neon city at night, cyberpunk style, floating vehicles, holographic advertisements, rain-slicked streets","n":1,"model":"flux.1-kontext-pro"}

                using (var genRequest = new HttpRequestMessage(HttpMethod.Post, generationUrl))
                {
                    genRequest.Headers.Add("Api-Key", apiKey);
                    var json = JsonSerializer.Serialize(generationBody);
                    genRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    var genResponse = await client.SendAsync(genRequest);
                    var genResult = await genResponse.Content.ReadAsStringAsync();

                    if (!genResponse.IsSuccessStatusCode)
                    {
                        var statusCode = (int)genResponse.StatusCode;
                        var reason = genResponse.ReasonPhrase ?? "";
                        var apiError = ExtractErrorMessage(genResult);
                        _logger.LogWarning("Image API error {StatusCode} {Reason}: {ApiError}", statusCode, reason, apiError);
                        throw new InvalidOperationException($"Image API error {statusCode} {reason}: {apiError}");
                    }

                    return await SaveResponseAndReturnBytes(genResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate image via Azure OpenAI");
                throw;
            }
        }

        /// <summary>
        /// Generates an image edit via Azure OpenAI Images Edits endpoint using multipart/form-data.
        /// Requires the Azure image service mode preset to include EditModelDeployment.
        /// </summary>
        private async Task<byte[]?> GenerateImageEditViaAzureOpenAI(
            string prompt,
            string size,
            int n,
            byte[] imageBytes,
            string imageContentType,
            string imageFileName,
            ServiceMode mode)
        {
            // Configuration: do not fallback silently if required settings are missing
            var endpoint = _configuration["AzureOpenAiImages:Endpoint"] ?? throw new InvalidOperationException("AzureOpenAiImages:Endpoint is not configured");
            var editDeployment = ReadServiceModePresetField(mode.RequestPresetJson, "EditModelDeployment")
                ?? throw new InvalidOperationException(
                    "ImageGeneration Azure service mode preset must include EditModelDeployment.");
            var apiVersion = _configuration["AzureOpenAiImages:ApiVersion"] ?? "2025-04-01-preview";
            var apiKey = _configuration["AzureOpenAiImages:ApiKey"] ?? throw new InvalidOperationException("AzureOpenAiImages:ApiKey is not configured");

            // Validate size parameter - only accept specific dimensions
            var validSizes = GetValidImageSizes(editDeployment);
            if (!validSizes.Contains(size))
            {
                throw new ArgumentException($"Invalid size '{size}'. Valid sizes are: {string.Join(", ", validSizes)}", nameof(size));
            }

            var basePath = $"openai/deployments/{editDeployment}/images";
            var url = $"{endpoint}{basePath}/edits?api-version={apiVersion}";

            using var client = _httpClientFactory.CreateClient();

            // Resize image if needed to fit within supported dimensions
            // This prevents 500 errors from the API when images are too large
            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(imageBytes, imageContentType);
            _logger.LogInformation("Image prepared for edit API. Size: {Size} bytes", imageBytes.Length);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(size), "size");
            form.Add(new StringContent(prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(n.ToString()), "n");
            // Some backends accept a model name; include it explicitly to match example
            form.Add(new StringContent(editDeployment.ToLowerInvariant()), "model");

            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            form.Add(imageContent, "image", string.IsNullOrWhiteSpace(imageFileName) ? "image.png" : imageFileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = form
            };

            request.Headers.Add("Api-Key", apiKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? string.Empty;
                var apiError = ExtractErrorMessage(result);
                _logger.LogWarning("Image edits API error {StatusCode} {Reason}: {ApiError}", statusCode, reason, apiError);
                throw new InvalidOperationException($"Image edits API error {statusCode} {reason}: {apiError}");
            }

            return await SaveResponseAndReturnBytes(result);
        }

        private async Task<byte[]?> GenerateImageEditViaGoogleGemini(
            string prompt,
            string size,
            int n,
            string outputFormat,
            byte[] imageBytes,
            string? imageContentType,
            string? imageFileName,
            string? modelId)
        {
            _ = outputFormat;
            var model = RequireImageModelId(
                GoogleGeminiProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for GoogleGeminiApi.");
            var apiKey = _configuration["GoogleGeminiApi:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("GoogleGeminiApi:ApiKey is required.");
            }

            var endpoint = $"https://generativelanguage.googleapis.com/v1beta/{NormalizeGoogleGeminiModelName(model)}:generateContent";

            imageBytes = AttachmentMessageBuilder.ResizeImageIfNeeded(
                imageBytes,
                string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType);
            var requestBody = new GoogleGeminiGenerateContentRequest(
                Contents:
                [
                    new GoogleGeminiContent(
                        "user",
                        [
                            new GoogleGeminiPart(Text: prompt),
                            new GoogleGeminiPart(
                                InlineData: new GoogleGeminiBlob(
                                    string.IsNullOrWhiteSpace(imageContentType) ? "application/octet-stream" : imageContentType,
                                    Convert.ToBase64String(imageBytes)))
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
                throw new InvalidOperationException($"Google Gemini image edit failed: {(int)response.StatusCode} {responseBody}");
            }

            return await SaveResponseAndReturnBytes(responseBody);
        }

        private async Task<byte[]?> GenerateImageEditViaHuggingFace(
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
            _ = size;
            _ = n;
            _ = outputFormat;
            var model = RequireImageModelId(
                HuggingFaceProviderSection,
                modelId,
                "Set ServiceModes.ImageGeneration model id for HuggingFace.");
            ValidateHuggingFaceImageModel(model, requestPresetJson);
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
            ValidateOpenRouterImageModel(model, requestPresetJson);
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

        private void ValidateHuggingFaceImageModel(string modelId, string? requestPresetJson)
        {
            var configured = ReadServiceModePresetField(requestPresetJson, "AllowedModels");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsModelAllowed(modelId, configured))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Hugging Face image model '{modelId}' is not in the ImageGeneration service-mode AllowedModels preset.");
            }

            if (modelId.Contains("image", StringComparison.OrdinalIgnoreCase)
                || modelId.Contains("diffusion", StringComparison.OrdinalIgnoreCase)
                || modelId.Contains("flux", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Hugging Face model '{modelId}' is not recognized as image-capable. " +
                "Set ImageGeneration service-mode AllowedModels to explicitly allow it.");
        }

        private void ValidateOpenRouterImageModel(string modelId, string? requestPresetJson)
        {
            var configured = ReadServiceModePresetField(requestPresetJson, "AllowedModels");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsModelAllowed(modelId, configured))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"OpenRouter image model '{modelId}' is not in the ImageGeneration service-mode AllowedModels preset.");
            }

            if (modelId.Contains("image", StringComparison.OrdinalIgnoreCase)
                || modelId.Contains("imagen", StringComparison.OrdinalIgnoreCase)
                || modelId.Contains("flux", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"OpenRouter model '{modelId}' is not recognized as image-capable/output_modalities=image capable. " +
                "Set ImageGeneration service-mode AllowedModels to explicitly allow it.");
        }

        private static bool IsModelAllowed(string modelId, string allowlistCsv)
        {
            var entries = allowlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                if (entry.EndsWith('*'))
                {
                    var prefix = entry[..^1];
                    if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (string.Equals(modelId, entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
            ValidateHuggingFaceImageModel(model, requestPresetJson);
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
            ValidateOpenRouterImageModel(model, requestPresetJson);
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
            //[Parameter(Description = "REQUIRED Image size for gpt-image. Valid: '1024x1024', '1024x1536', '1536x1024', 'auto'.")] string size,
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
