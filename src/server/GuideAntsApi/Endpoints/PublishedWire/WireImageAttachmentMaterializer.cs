using GuideAntsApi.Models.Conversations;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services.Components;
using Microsoft.AspNetCore.Http;

namespace GuideAntsApi.Endpoints.PublishedWire;

internal static class WireImageAttachmentMaterializer
{
    private const string WireAttachmentFolder = "Output/.wire-attachments";
    private const int MaxRemoteImageBytes = 20 * 1024 * 1024;

    internal static async Task<List<AttachmentDto>> MaterializeAsync(
        INotebookFileService notebookFileService,
        Guid projectId,
        Guid notebookId,
        IReadOnlyList<string> imageUrls,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var attachments = new List<AttachmentDto>();
        if (imageUrls == null || imageUrls.Count == 0)
        {
            return attachments;
        }

        foreach (var rawUrl in imageUrls)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                continue;
            }

            var (bytes, contentType, extension) = await ResolveImageBytesAsync(
                rawUrl.Trim(),
                httpClient,
                cancellationToken).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0)
            {
                continue;
            }

            var fileName = $"wire-image-{Guid.NewGuid():N}.{extension}";
            await using var memory = new MemoryStream(bytes, writable: false);
            var formFile = new FormFile(memory, 0, memory.Length, "files", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType
            };
            var files = new FormFileCollection { formFile };
            var uploaded = await notebookFileService.UploadFilesAsync(
                projectId,
                notebookId,
                files,
                WireAttachmentFolder,
                index: false,
                forceMarkdownExtraction: false).ConfigureAwait(false);

            foreach (var file in uploaded)
            {
                attachments.Add(new AttachmentDto(file.Id, ContentUploadType.ImageFile, file.RelativePath));
            }
        }

        return attachments;
    }

    private static async Task<(byte[]? Bytes, string ContentType, string Extension)> ResolveImageBytesAsync(
        string imageUrl,
        HttpClient? httpClient,
        CancellationToken cancellationToken)
    {
        if (imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDataUri(imageUrl);
        }

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, "application/octet-stream", "bin");
        }

        if (httpClient == null)
        {
            return (null, "application/octet-stream", "bin");
        }

        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (null, "application/octet-stream", "bin");
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxRemoteImageBytes)
        {
            return (null, "application/octet-stream", "bin");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        var copyBuffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(copyBuffer.AsMemory(0, copyBuffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxRemoteImageBytes)
            {
                return (null, "application/octet-stream", "bin");
            }

            await buffer.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var contentType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType;
        return (buffer.ToArray(), contentType, ExtensionFromContentType(contentType));
    }

    private static (byte[]? Bytes, string ContentType, string Extension) ParseDataUri(string dataUri)
    {
        var commaIndex = dataUri.IndexOf(',');
        if (commaIndex <= 5)
        {
            return (null, "application/octet-stream", "bin");
        }

        var meta = dataUri[5..commaIndex];
        var payload = dataUri[(commaIndex + 1)..];
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, "application/octet-stream", "bin");
        }

        var contentType = "application/octet-stream";
        var semiIndex = meta.IndexOf(';');
        if (semiIndex > 0)
        {
            contentType = meta[..semiIndex];
        }
        else if (!string.IsNullOrWhiteSpace(meta) &&
                 !meta.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            contentType = meta;
        }

        if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return (null, contentType, ExtensionFromContentType(contentType));
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            return (bytes, contentType, ExtensionFromContentType(contentType));
        }
        catch (FormatException)
        {
            return (null, contentType, ExtensionFromContentType(contentType));
        }
    }

    private static string ExtensionFromContentType(string contentType)
    {
        var normalized = contentType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/bmp" => "bmp",
            "image/tiff" => "tiff",
            _ => "bin"
        };
    }
}
