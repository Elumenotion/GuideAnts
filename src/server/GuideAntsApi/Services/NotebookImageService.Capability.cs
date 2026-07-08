using AntRunner.ToolCalling;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Services;

public partial class NotebookImageService
{
    public async Task<byte[]?> GenerateImageBytesAsync(
        string prompt,
        string size = "1024x1024",
        int n = 1,
        string outputFormat = "png",
        InvocationContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt cannot be null or empty.", nameof(prompt));
        }

        if (context?.ProjectId == null || context.NotebookId == Guid.Empty)
        {
            throw new InvalidOperationException("Project ID and Notebook ID are required for image generation.");
        }

        var mode = await ResolveImageGenerationModeAsync(cancellationToken).ConfigureAwait(false);
        var imageProvider = ResolveImageProviderId(mode);

        var imageAttachment = _serviceProvider != null
            ? await MediaAttachmentHelper.TryGetFirstImageAttachmentForCurrentUserMessageAsync(
                _serviceProvider,
                context.ConversationId)
            : null;

        if (imageAttachment != null)
        {
            var (imageContent, contentType, sourceFileName) = imageAttachment.Value;
            return imageProvider switch
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

        return imageProvider switch
        {
            ImageProviderLocal => await GenerateImageViaLocalSd(prompt, size, n, outputFormat),
            ImageProviderCloud => await GenerateImageViaAzureOpenAI(prompt, size, n, outputFormat, mode),
            ImageProviderGoogle => await GenerateImageViaGoogleGemini(prompt, size, n, outputFormat, mode.ModelId),
            ImageProviderHuggingFace => await GenerateImageViaHuggingFace(
                prompt,
                size,
                n,
                outputFormat,
                mode.ModelId,
                mode.RequestPresetJson),
            ImageProviderOpenRouter => await GenerateImageViaOpenRouter(
                prompt,
                size,
                n,
                outputFormat,
                mode.ModelId,
                mode.RequestPresetJson),
            ImageProviderOpenAi => await GenerateImageViaOpenAi(prompt, size, n, mode.ModelId),
            _ => throw new RoutingException(
                RoutingErrorCodes.ProviderNotReady,
                $"Image provider '{imageProvider}' is not recognized.",
                action: "Fix the service mode provider configuration.",
                serviceId: RoutedServiceNames.ImageGeneration,
                providerSection: imageProvider)
        };
    }

    public async Task WriteImageBytesToNotebookOutputAsync(
        byte[] imageBytes,
        string filename,
        InvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("Image bytes cannot be empty.", nameof(imageBytes));
        }

        if (context.ProjectId == Guid.Empty || context.NotebookId == Guid.Empty)
        {
            throw new InvalidOperationException("Project ID and Notebook ID are required.");
        }

        var sanitizedFilename = SanitizeGeneratedImageFilename(filename, "png");
        var storageRoot = ResolveStorageRoot();
        var filePath = NotebookRunOutputWriter.BuildOutputFilePath(context, storageRoot, sanitizedFilename);
        await File.WriteAllBytesAsync(filePath, imageBytes, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Image saved to notebook output without database sync: {FilePath}, Size: {Size} bytes",
            LogValueSanitizer.Sanitize(filePath),
            imageBytes.Length);
    }

    private string ResolveStorageRoot()
    {
        if (_serviceProvider != null)
        {
            using var scope = _serviceProvider.CreateScope();
            var cfg = scope.ServiceProvider.GetService<IConfiguration>();
            if (cfg != null)
            {
                return NotebookRunOutputWriter.ResolveStorageRoot(cfg);
            }
        }

        return NotebookRunOutputWriter.ResolveStorageRoot(_configuration);
    }

    private static string SanitizeGeneratedImageFilename(string filename, string outputFormat)
    {
        filename = Path.GetFileName(filename);
        var expectedExtension = $".{outputFormat.ToLowerInvariant()}";
        if (!filename.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            filename += expectedExtension;
        }

        return filename;
    }
}
