using System.Net.Http.Headers;
using GuideAntsApi.BackgroundJobs.Options;

namespace GuideAntsApi.BackgroundJobs.Services;

internal static class DoclingServeFormContentBuilder
{
    public static MultipartFormDataContent BuildConversionForm(
        Stream content,
        string fileName,
        DocumentIntelligenceOptions options)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("md"), "to_formats");

        var fromFormat = ResolveFromFormat(fileName);
        if (!string.IsNullOrWhiteSpace(fromFormat))
        {
            multipart.Add(new StringContent(fromFormat), "from_formats");
        }

        AddOptionalBool(multipart, "do_ocr", options.DoclingDoOcr);
        AddOptionalBool(multipart, "force_ocr", options.DoclingForceOcr);
        AddOptionalString(multipart, "ocr_preset", options.DoclingOcrPreset);
        AddOptionalString(multipart, "ocr_lang", options.DoclingOcrLang);
        AddOptionalString(multipart, "pdf_backend", options.DoclingPdfBackend);
        AddOptionalString(multipart, "table_mode", options.DoclingTableMode);
        AddOptionalBool(multipart, "table_cell_matching", options.DoclingTableCellMatching);
        AddOptionalString(multipart, "image_export_mode", options.DoclingImageExportMode);
        AddOptionalString(multipart, "pipeline", options.DoclingPipeline);
        AddOptionalBool(multipart, "do_code_enrichment", options.DoclingDoCodeEnrichment);
        AddOptionalBool(multipart, "do_formula_enrichment", options.DoclingDoFormulaEnrichment);
        AddOptionalBool(multipart, "do_picture_classification", options.DoclingDoPictureClassification);
        AddOptionalBool(multipart, "do_picture_description", options.DoclingDoPictureDescription);
        AddOptionalString(multipart, "picture_description_preset", options.DoclingPictureDescriptionPreset);

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(fileName));
        multipart.Add(streamContent, "files", Path.GetFileName(fileName));

        return multipart;
    }

    public static void ApplyAuthHeaders(HttpRequestMessage request, DocumentIntelligenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DoclingApiKey))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation("X-Api-Key", options.DoclingApiKey.Trim());
    }

    public static int ResolvePerRequestTimeoutSeconds(DocumentIntelligenceOptions options)
    {
        var overallTimeout = Math.Max(1, options.TimeoutSeconds);
        return Math.Clamp(overallTimeout / 4, 30, 120);
    }

    private static void AddOptionalBool(MultipartFormDataContent multipart, string fieldName, bool? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        multipart.Add(new StringContent(value.Value ? "true" : "false"), fieldName);
    }

    private static void AddOptionalString(MultipartFormDataContent multipart, string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        multipart.Add(new StringContent(value.Trim()), fieldName);
    }

    private static string? ResolveFromFormat(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "pdf",
            ".docx" => "docx",
            ".xlsx" => "xlsx",
            ".pptx" => "pptx",
            ".html" or ".htm" => "html",
            ".jpg" or ".jpeg" => "jpg",
            ".png" => "png",
            ".bmp" => "bmp",
            ".tiff" => "tiff",
            ".heif" => "heif",
            _ => null
        };
    }

    private static string GetContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".tiff" => "image/tiff",
            ".heif" => "image/heif",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream"
        };
    }
}
