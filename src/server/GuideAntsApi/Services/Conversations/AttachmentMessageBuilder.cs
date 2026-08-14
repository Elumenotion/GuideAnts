using AntRunner.Chat.Abstractions;
using GuideAntsApi.DataModel.Models;
using ChatMessageRole = AntRunner.Chat.Abstractions.ChatRole;
using GuideAntsApi.Services.Components;
using GuideAntsApi.Services.Core;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Exceptions;
using GuideAntsApi.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Webp;

namespace GuideAntsApi.Services.Conversations;

/// <summary>
/// Utility class for converting NotebookFile attachments to OpenAI messages.
/// Centralizes the attachment processing logic used by both ConversationService and Agent.
/// </summary>
public static class AttachmentMessageBuilder
{
    /// <summary>
    /// Creates OpenAI messages from a NotebookFile attachment following all attachment rules.
    /// </summary>
    public static async Task<List<ChatMessage>> CreateMessagesFromNotebookFileAsync(
        NotebookFile notebookFile,
        INotebookFileService? notebookFileService,
        IMarkdownExtractionService? markdownExtractionService,
        string storagePath,
        CancellationToken cancellationToken,
        int maxInlineMarkdownCharacters = MarkdownAttachmentOptions.DefaultMaxInlineCharacters,
        ILogger? attachmentMarkdownLogger = null)
    {
        var messages = new List<ChatMessage>();
        if (notebookFileService == null) return messages;

        try
        {
            // Rule 1: Always add file path
            string attachmentPath = GetAttachmentPath(notebookFile, storagePath);
            messages.Add(new ChatMessage(ChatMessageRole.User, $"Attachment: {attachmentPath}"));

            var resTuple = await notebookFileService.GetFileContentStreamAsync(notebookFile.Id, cancellationToken);
            if (resTuple == null) return messages;
            var (stream, contentType, fileName) = resTuple.Value;

            var uploadType = DetermineUploadType(fileName);
            var shouldHaveMarkdownShadow = ShouldHaveMarkdownShadow(fileName);

            var bytes = await StreamToBytesAsync(stream, cancellationToken);

            // Rule 2: For files that should have markdown shadow, wait for it and add content
            // This is supplementary and should not block adding the primary content (e.g., image bytes)
            if (markdownExtractionService != null && shouldHaveMarkdownShadow)
            {
                try
                {
                    // Wait up to 30 seconds for shadow to be ready
                    var markdownShadow = await WaitForMarkdownShadowAsync(
                        markdownExtractionService,
                        notebookFile.Id,
                        fileName,
                        cancellationToken);
                    
                    if (markdownShadow?.Status == MarkdownExtractionStatus.Completed)
                    {
                        var markdownContent = await markdownExtractionService.GetNotebookMarkdownContentAsync(notebookFile.Id, cancellationToken);
                        if (markdownContent.HasValue && markdownContent.Value.Content != null)
                        {
                            using var reader = new StreamReader(markdownContent.Value.Content);
                            var markdownText = await reader.ReadToEndAsync();
                            var userText = AttachmentMarkdownChatPolicy.BuildMarkdownAttachmentUserText(
                                markdownText,
                                fileName,
                                attachmentPath,
                                maxInlineMarkdownCharacters,
                                attachmentMarkdownLogger,
                                notebookFile.Id);
                            messages.Add(new ChatMessage(ChatMessageRole.User, userText));
                        }
                    }
                }
                catch (AttachmentNotReadyException)
                {
                    // Markdown extraction is not ready yet, but continue to add primary content
                    // The exception will be re-thrown at the end if this is a non-image file
                    if (uploadType != ContentUploadType.ImageFile && uploadType != ContentUploadType.TextFile)
                    {
                        throw;
                    }
                }
            }

            // Rule 3: If image, add base64 image (resize if needed to fit within supported dimensions)
            if (uploadType == ContentUploadType.ImageFile)
            {
                bytes = ResizeImageIfNeeded(bytes, contentType);
                var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
                messages.Add(new ChatMessage(ChatMessageRole.User, new List<ChatContent> { new ChatContent(new ChatImageUrl(dataUrl)) }));
            }
            // Rule 4: If text file without shadow requirement, inline it directly
            // Only add text content for known text extensions to avoid adding binary content for unknown file types
            else if (uploadType == ContentUploadType.TextFile && !shouldHaveMarkdownShadow && IsKnownTextExtension(Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant()))
            {
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                var userText = BuildTextAttachmentUserText(
                    text,
                    fileName,
                    attachmentPath,
                    maxInlineMarkdownCharacters,
                    attachmentMarkdownLogger,
                    notebookFile.Id);
                messages.Add(new ChatMessage(ChatMessageRole.User, userText));
            }
            // Rule 5: For unknown extensions, inline only if the payload reliably decodes as text.
            else if (uploadType == ContentUploadType.SandboxFile && TryDecodeUnknownText(bytes, out var inferredText))
            {
                var userText = BuildTextAttachmentUserText(
                    inferredText,
                    fileName,
                    attachmentPath,
                    maxInlineMarkdownCharacters,
                    attachmentMarkdownLogger,
                    notebookFile.Id);
                messages.Add(new ChatMessage(ChatMessageRole.User, userText));
            }

            return messages;
        }
        catch
        {
            return messages;
        }
    }

    /// <summary>
    /// Creates OpenAI Content items from a NotebookFile attachment.
    /// </summary>
    public static async Task<List<ChatContent>> CreateContentFromNotebookFileAsync(
        NotebookFile notebookFile,
        INotebookFileService? notebookFileService,
        IMarkdownExtractionService? markdownExtractionService,
        string storagePath,
        CancellationToken cancellationToken,
        int maxInlineMarkdownCharacters = MarkdownAttachmentOptions.DefaultMaxInlineCharacters,
        ILogger? attachmentMarkdownLogger = null)
    {
        var contents = new List<ChatContent>();
        if (notebookFileService == null) return contents;

        try
        {
            // Rule 1: Always add file path
            string attachmentPath = GetAttachmentPath(notebookFile, storagePath);
            contents.Add(new ChatContent($"Attachment: {attachmentPath}"));

            var resTuple = await notebookFileService.GetFileContentStreamAsync(notebookFile.Id, cancellationToken);
            if (resTuple == null) return contents;
            var (stream, contentType, fileName) = resTuple.Value;

            var uploadType = DetermineUploadType(fileName);
            var shouldHaveMarkdownShadow = ShouldHaveMarkdownShadow(fileName);

            var bytes = await StreamToBytesAsync(stream, cancellationToken);

            // Rule 2: For files that should have markdown shadow, wait for it and add content
            // This is supplementary and should not block adding the primary content (e.g., image bytes)
            if (markdownExtractionService != null && shouldHaveMarkdownShadow)
            {
                try
                {
                    // Wait up to 30 seconds for shadow to be ready
                    var markdownShadow = await WaitForMarkdownShadowAsync(
                        markdownExtractionService,
                        notebookFile.Id,
                        fileName,
                        cancellationToken);
                    
                    if (markdownShadow?.Status == MarkdownExtractionStatus.Completed)
                    {
                        var markdownContent = await markdownExtractionService.GetNotebookMarkdownContentAsync(notebookFile.Id, cancellationToken);
                        if (markdownContent.HasValue && markdownContent.Value.Content != null)
                        {
                            using var reader = new StreamReader(markdownContent.Value.Content);
                            var markdownText = await reader.ReadToEndAsync();
                            var userText = AttachmentMarkdownChatPolicy.BuildMarkdownAttachmentUserText(
                                markdownText,
                                fileName,
                                attachmentPath,
                                maxInlineMarkdownCharacters,
                                attachmentMarkdownLogger,
                                notebookFile.Id);
                            contents.Add(new ChatContent(userText));
                        }
                    }
                }
                catch (AttachmentNotReadyException)
                {
                    // Markdown extraction is not ready yet, but continue to add primary content
                    // The exception will be re-thrown at the end if this is a non-image file
                    if (uploadType != ContentUploadType.ImageFile && uploadType != ContentUploadType.TextFile)
                    {
                        throw;
                    }
                }
            }

            // Rule 3: If image, add base64 image (resize if needed to fit within supported dimensions)
            if (uploadType == ContentUploadType.ImageFile)
            {
                bytes = ResizeImageIfNeeded(bytes, contentType);
                var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
                contents.Add(new ChatContent(new ChatImageUrl(dataUrl)));
            }
            // Rule 4: If text file without shadow requirement, inline it directly
            // Only add text content for known text extensions to avoid adding binary content for unknown file types
            else if (uploadType == ContentUploadType.TextFile && !shouldHaveMarkdownShadow && IsKnownTextExtension(Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant()))
            {
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                var userText = BuildTextAttachmentUserText(
                    text,
                    fileName,
                    attachmentPath,
                    maxInlineMarkdownCharacters,
                    attachmentMarkdownLogger,
                    notebookFile.Id);
                contents.Add(new ChatContent(userText));
            }
            // Rule 5: For unknown extensions, inline only if the payload reliably decodes as text.
            else if (uploadType == ContentUploadType.SandboxFile && TryDecodeUnknownText(bytes, out var inferredText))
            {
                var userText = BuildTextAttachmentUserText(
                    inferredText,
                    fileName,
                    attachmentPath,
                    maxInlineMarkdownCharacters,
                    attachmentMarkdownLogger,
                    notebookFile.Id);
                contents.Add(new ChatContent(userText));
            }

            return contents;
        }
        catch
        {
            return contents;
        }
    }

    private static string GetAttachmentPath(NotebookFile notebookFile, string _) =>
        // Sandbox CWD is Output/ (unpublished). Match ContextOptionFilesResolver so
        // attachment path text is usable by tools the same way as [@files] listings.
        // Output/foo/bar → foo/bar; data/file.csv → ../data/file.csv
        ContextOptionFilesResolver.ToCwdRelativePath(notebookFile.RelativePath, isPublished: false);

    private static ContentUploadType DetermineUploadType(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "tiff" or "webp" => ContentUploadType.ImageFile,
            "wav" or "mp3" or "flac" or "aac" or "ogg" or "m4a" => ContentUploadType.AudioFile,
            _ => IsKnownTextExtension(ext) ? ContentUploadType.TextFile : ContentUploadType.SandboxFile
        };
    }

    /// <summary>
    /// Determines if a file extension represents a known text file type that can be safely inlined as UTF-8 text.
    /// </summary>
    private static bool IsKnownTextExtension(string extension)
    {
        var knownTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "md", "markdown", "csv", "json", "xml", "html", "htm", "css", "js", "ts", "tsx",
            "py", "cs", "java", "c", "cpp", "cc", "cxx", "h", "hpp", "hxx",
            "go", "rs", "rb", "php", "sh", "bash", "ps1", "bat", "cmd",
            "yaml", "yml", "toml", "ini", "cfg", "conf", "log", "sql", "r", "m", "swift", "kt", "scala"
        };
        return knownTextExtensions.Contains(extension);
    }

    private static bool ShouldHaveMarkdownShadow(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".docx", ".xlsx", ".pptx", ".html",
            ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".heif",
            ".wav", ".mp3", ".flac", ".aac", ".ogg", ".m4a",
            ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v"
        };
        return supportedExtensions.Contains(extension);
    }

    private static string BuildTextAttachmentUserText(
        string text,
        string fileName,
        string attachmentPath,
        int maxInlineCharacters,
        ILogger? logger,
        Guid? notebookFileId = null)
    {
        if (text.Length > maxInlineCharacters)
        {
            logger?.LogInformation(
                "Attachment text too large to inline for {FileName} (NotebookFileId: {NotebookFileId}): {TextLength} chars (max {MaxInlineCharacters}); using path reference only",
                fileName, notebookFileId, text.Length, maxInlineCharacters);

            return
                $"Text content of {fileName} is too large to include inline ({text.Length} characters; limit {maxInlineCharacters}). " +
                $"Use the file at `{attachmentPath}` (see the Attachment line above).";
        }

        return $"The {fileName} file contains:\n\n---\n{text}";
    }

    private static bool TryDecodeUnknownText(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Length == 0)
        {
            return true;
        }

        if (TryDecodeByBom(bytes, out text))
        {
            return IsMostlyText(text);
        }

        // For unknown files without BOM, a null byte is a strong binary signal.
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        var utf8Strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        if (!TryDecodeWithEncoding(bytes, 0, utf8Strict, out text))
        {
            return false;
        }

        return IsMostlyText(text);
    }

    private static bool TryDecodeByBom(byte[] bytes, out string text)
    {
        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return TryDecodeWithEncoding(
                bytes,
                3,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                out text);
        }

        // UTF-32 LE BOM
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return TryDecodeWithEncoding(
                bytes,
                4,
                new System.Text.UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
                out text);
        }

        // UTF-32 BE BOM
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return TryDecodeWithEncoding(
                bytes,
                4,
                new System.Text.UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
                out text);
        }

        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return TryDecodeWithEncoding(
                bytes,
                2,
                new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
                out text);
        }

        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return TryDecodeWithEncoding(
                bytes,
                2,
                new System.Text.UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
                out text);
        }

        text = string.Empty;
        return false;
    }

    private static bool TryDecodeWithEncoding(byte[] bytes, int offset, System.Text.Encoding encoding, out string text)
    {
        try
        {
            var count = Math.Max(0, bytes.Length - offset);
            text = encoding.GetString(bytes, offset, count);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
        catch (ArgumentException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool IsMostlyText(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        var disallowedControlCount = 0;
        foreach (var ch in text)
        {
            // Replacement char signals decoding uncertainty.
            if (ch == '\uFFFD')
            {
                return false;
            }

            if (char.IsControl(ch) && ch != '\t' && ch != '\n' && ch != '\r')
            {
                disallowedControlCount++;
            }
        }

        var disallowedRatio = (double)disallowedControlCount / text.Length;
        return disallowedRatio <= 0.01d;
    }

    private static async Task<byte[]> StreamToBytesAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>
    /// Resizes an image if it exceeds the maximum dimensions supported by Azure OpenAI vision models.
    /// Supported dimensions: 1024x1024 (square), 1024x1792 (portrait), 1792x1024 (landscape)
    /// </summary>
    /// <param name="imageBytes">Original image bytes</param>
    /// <param name="contentType">Content type of the image</param>
    /// <returns>Resized image bytes if resizing was needed, otherwise original bytes</returns>
    public static byte[] ResizeImageIfNeeded(byte[] imageBytes, string contentType)
    {
        try
        {
            using var image = Image.Load(imageBytes);
            var width = image.Width;
            var height = image.Height;

            // Check if resizing is needed
            var (targetWidth, targetHeight) = DetermineBestSizeForImage(width, height);
            
            if (width == targetWidth && height == targetHeight)
            {
                // No resizing needed
                return imageBytes;
            }

            if (width <= targetWidth && height <= targetHeight)
            {
                // Image is already smaller than target dimensions
                return imageBytes;
            }

            // Calculate resize dimensions maintaining aspect ratio
            var (resizeWidth, resizeHeight) = CalculateResizeDimensions(width, height, targetWidth, targetHeight);

            // Resize the image
            image.Mutate(x => x.Resize(resizeWidth, resizeHeight));

            // Encode back to bytes with appropriate format
            using var outputStream = new MemoryStream();
            
            // Determine encoder based on content type
            var encoder = contentType.ToLowerInvariant() switch
            {
                "image/png" => (SixLabors.ImageSharp.Formats.IImageEncoder)new PngEncoder(),
                "image/jpeg" or "image/jpg" => new JpegEncoder { Quality = 90 },
                "image/gif" => new GifEncoder(),
                "image/bmp" => new BmpEncoder(),
                "image/webp" => new WebpEncoder(),
                _ => new PngEncoder() // Default to PNG
            };

            image.Save(outputStream, encoder);
            return outputStream.ToArray();
        }
        catch
        {
            // If resizing fails, return original bytes
            return imageBytes;
        }
    }

    /// <summary>
    /// Determines the best target size based on image dimensions.
    /// Returns one of: (1024, 1024), (1024, 1792), or (1792, 1024)
    /// </summary>
    private static (int width, int height) DetermineBestSizeForImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return (1024, 1024);
        }

        double aspectRatio = (double)width / height;

        // Calculate aspect ratios for our available sizes
        const double squareRatio = 1.0;        // 1024/1024 = 1.0
        const double portraitRatio = 0.571;    // 1024/1792 = 0.571
        const double landscapeRatio = 1.75;    // 1792/1024 = 1.75

        // Find the closest match
        double squareDiff = Math.Abs(aspectRatio - squareRatio);
        double portraitDiff = Math.Abs(aspectRatio - portraitRatio);
        double landscapeDiff = Math.Abs(aspectRatio - landscapeRatio);

        if (portraitDiff < squareDiff && portraitDiff < landscapeDiff)
        {
            return (1024, 1792);
        }
        else if (landscapeDiff < squareDiff && landscapeDiff < portraitDiff)
        {
            return (1792, 1024);
        }
        else
        {
            return (1024, 1024);
        }
    }

    /// <summary>
    /// Calculates resize dimensions that maintain aspect ratio and fit within target bounds
    /// </summary>
    private static (int width, int height) CalculateResizeDimensions(int originalWidth, int originalHeight, int targetWidth, int targetHeight)
    {
        double widthRatio = (double)targetWidth / originalWidth;
        double heightRatio = (double)targetHeight / originalHeight;
        
        // Use the smaller ratio to ensure image fits within target bounds
        double ratio = Math.Min(widthRatio, heightRatio);

        int newWidth = (int)(originalWidth * ratio);
        int newHeight = (int)(originalHeight * ratio);

        return (newWidth, newHeight);
    }

    /// <summary>
    /// Waits for a markdown shadow to complete extraction, polling every 1 second for up to 30 seconds.
    /// Throws AttachmentNotReadyException if shadow is not completed within the timeout period.
    /// Returns the completed shadow if successful.
    /// </summary>
    private static async Task<NotebookFileMarkdownShadow?> WaitForMarkdownShadowAsync(
        IMarkdownExtractionService markdownExtractionService,
        Guid notebookFileId,
        string fileName,
        CancellationToken cancellationToken)
    {
        const int maxWaitSeconds = 30;
        const int pollIntervalMs = 1000;

        for (int attempt = 0; attempt < maxWaitSeconds; attempt++)
        {
            var shadow = await markdownExtractionService.GetNotebookMarkdownShadowAsync(notebookFileId, cancellationToken);
            
            // If shadow is completed, return it immediately
            if (shadow?.Status == MarkdownExtractionStatus.Completed)
            {
                return shadow;
            }

            // If shadow is skipped (unsupported type or doesn't need processing), return immediately
            if (shadow?.Status == MarkdownExtractionStatus.Skipped)
            {
                return shadow;
            }

            // If shadow failed, throw exception immediately
            if (shadow?.Status == MarkdownExtractionStatus.Failed)
            {
                throw new AttachmentNotReadyException(
                    notebookFileId,
                    fileName,
                    $"The attached file '{fileName}' failed to be indexed: {shadow.ErrorMessage ?? "Unknown error"}. Please try uploading the file again.");
            }

            // If this was the last attempt, throw timeout exception
            if (attempt == maxWaitSeconds - 1)
            {
                throw new AttachmentNotReadyException(
                    notebookFileId,
                    fileName,
                    $"The attached file '{fileName}' is still being indexed. Please wait a moment and try again.");
            }

            // Wait before next poll (unless we're cancelling)
            await Task.Delay(pollIntervalMs, cancellationToken);
        }

        // This should never be reached due to the check in the loop
        throw new AttachmentNotReadyException(notebookFileId, fileName);
    }
}

