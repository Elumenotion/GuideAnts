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

        await EnsureTurnExecutionCanPublishAsync(context, cancellationToken).ConfigureAwait(false);

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
                    imageFileName: sourceFileName,
                    cancellationToken: cancellationToken),
                ImageProviderCloud => await GenerateImageEditViaAzureOpenAI(
                    prompt: prompt,
                    size: size,
                    n: n,
                    imageBytes: imageContent,
                    imageContentType: contentType,
                    imageFileName: sourceFileName,
                    mode: mode,
                    cancellationToken: cancellationToken),
                ImageProviderGoogle => await GenerateImageEditViaGoogleGemini(
                    prompt: prompt,
                    size: size,
                    n: n,
                    outputFormat: outputFormat,
                    imageBytes: imageContent,
                    imageContentType: contentType,
                    imageFileName: sourceFileName,
                    modelId: mode.ModelId,
                    cancellationToken: cancellationToken),
                ImageProviderHuggingFace => await GenerateImageEditViaHuggingFace(
                    prompt: prompt,
                    size: size,
                    n: n,
                    outputFormat: outputFormat,
                    imageBytes: imageContent,
                    imageContentType: contentType,
                    imageFileName: sourceFileName,
                    modelId: mode.ModelId,
                    requestPresetJson: mode.RequestPresetJson,
                    cancellationToken: cancellationToken),
                ImageProviderOpenRouter => await GenerateImageEditViaOpenRouter(
                    prompt: prompt,
                    size: size,
                    n: n,
                    outputFormat: outputFormat,
                    imageBytes: imageContent,
                    imageContentType: contentType,
                    imageFileName: sourceFileName,
                    modelId: mode.ModelId,
                    requestPresetJson: mode.RequestPresetJson,
                    cancellationToken: cancellationToken),
                ImageProviderOpenAi => await GenerateImageEditViaOpenAi(
                    prompt: prompt,
                    size: size,
                    n: n,
                    imageBytes: imageContent,
                    imageContentType: contentType,
                    imageFileName: sourceFileName,
                    modelId: mode.ModelId,
                    cancellationToken: cancellationToken),
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
            ImageProviderLocal => await GenerateImageViaLocalSd(prompt, size, n, outputFormat, cancellationToken),
            ImageProviderCloud => await GenerateImageViaAzureOpenAI(prompt, size, n, outputFormat, mode, cancellationToken),
            ImageProviderGoogle => await GenerateImageViaGoogleGemini(prompt, size, n, outputFormat, mode.ModelId, cancellationToken),
            ImageProviderHuggingFace => await GenerateImageViaHuggingFace(
                prompt,
                size,
                n,
                outputFormat,
                mode.ModelId,
                mode.RequestPresetJson,
                cancellationToken),
            ImageProviderOpenRouter => await GenerateImageViaOpenRouter(
                prompt,
                size,
                n,
                outputFormat,
                mode.ModelId,
                mode.RequestPresetJson,
                cancellationToken),
            ImageProviderOpenAi => await GenerateImageViaOpenAi(prompt, size, n, mode.ModelId, cancellationToken),
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

        await EnsureTurnExecutionCanPublishAsync(context, cancellationToken).ConfigureAwait(false);
        var sanitizedFilename = SanitizeGeneratedImageFilename(filename, "png");
        var storageRoot = ResolveStorageRoot();
        var filePath = NotebookRunOutputWriter.BuildOutputFilePath(context, storageRoot, sanitizedFilename);
        await WriteImageFileAtomicallyAsync(filePath, imageBytes, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Image saved to notebook output without database sync: {FilePath}, Size: {Size} bytes",
            LogValueSanitizer.Sanitize(filePath),
            imageBytes.Length);
    }

    private static async Task WriteImageFileAtomicallyAsync(
        string filePath,
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Image output directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, imageBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A failed cleanup must not hide the provider or cancellation result.
            }
        }
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
