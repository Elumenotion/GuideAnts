using System.Net;
using System.Text;
using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class DoclingServeFormContentBuilderTests
{
    [TestMethod]
    public void BuildConversionForm_IncludesConfiguredDoclingOptions()
    {
        var options = new DocumentIntelligenceOptions
        {
            DoclingDoOcr = true,
            DoclingForceOcr = false,
            DoclingOcrPreset = "rapidocr",
            DoclingOcrLang = "en",
            DoclingPdfBackend = "pypdfium2",
            DoclingTableMode = "accurate",
            DoclingTableCellMatching = true,
            DoclingImageExportMode = "placeholder",
            DoclingPipeline = "standard",
            DoclingDoCodeEnrichment = true,
            DoclingDoFormulaEnrichment = false,
            DoclingDoPictureClassification = true,
            DoclingDoPictureDescription = false,
            DoclingPictureDescriptionPreset = "smolvlm"
        };

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("pdf"));
        using var form = DoclingServeFormContentBuilder.BuildConversionForm(stream, "report.pdf", options);

        var fields = ReadMultipartFields(form);
        fields.Should().ContainKey("to_formats").WhoseValue.Should().Be("md");
        fields.Should().ContainKey("from_formats").WhoseValue.Should().Be("pdf");
        fields["do_ocr"].Should().Be("true");
        fields["force_ocr"].Should().Be("false");
        fields["ocr_preset"].Should().Be("rapidocr");
        fields["ocr_lang"].Should().Be("en");
        fields["pdf_backend"].Should().Be("pypdfium2");
        fields["table_mode"].Should().Be("accurate");
        fields["table_cell_matching"].Should().Be("true");
        fields["image_export_mode"].Should().Be("placeholder");
        fields["pipeline"].Should().Be("standard");
        fields["do_code_enrichment"].Should().Be("true");
        fields["do_formula_enrichment"].Should().Be("false");
        fields["do_picture_classification"].Should().Be("true");
        fields["do_picture_description"].Should().Be("false");
        fields["picture_description_preset"].Should().Be("smolvlm");
    }

    [TestMethod]
    public void ApplyAuthHeaders_AddsApiKeyHeader_WhenConfigured()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://docling:5001/v1/convert/file/async");
        DoclingServeFormContentBuilder.ApplyAuthHeaders(
            request,
            new DocumentIntelligenceOptions { DoclingApiKey = "secret-key" });

        request.Headers.TryGetValues("X-Api-Key", out var values).Should().BeTrue();
        values!.Single().Should().Be("secret-key");
    }

    [TestMethod]
    public void ResolvePerRequestTimeoutSeconds_ClampsToConfiguredOverallTimeout()
    {
        DoclingServeFormContentBuilder.ResolvePerRequestTimeoutSeconds(
                new DocumentIntelligenceOptions { TimeoutSeconds = 300 })
            .Should().Be(75);

        DoclingServeFormContentBuilder.ResolvePerRequestTimeoutSeconds(
                new DocumentIntelligenceOptions { TimeoutSeconds = 60 })
            .Should().Be(30);
    }

    private static Dictionary<string, string> ReadMultipartFields(MultipartFormDataContent form)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var content in form)
        {
            if (content.Headers.ContentDisposition?.FileName is not null)
            {
                continue;
            }

            var name = content.Headers.ContentDisposition?.Name?.Trim('"');
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            fields[name] = content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        return fields;
    }
}
