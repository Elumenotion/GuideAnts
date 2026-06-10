using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.BackgroundJobs;

/// <summary>
/// Validation/error-handling coverage for the Azure Document Intelligence extractor.
/// The actual <c>AnalyzeDocumentAsync</c> call uses the Azure SDK pipeline (no injectable
/// HttpClient) and therefore requires a live Azure DI endpoint; that path is intentionally
/// not exercised here. We cover the configuration guards and request-preset parsing branches.
/// </summary>
[TestClass]
public sealed class AzureDocumentIntelligenceExtractorTests
{
    private static ServiceMode ModeWithPreset(string? presetJson) => new(
        ModeId: "default",
        ProviderSection: "AzureDocumentIntelligence",
        ModelId: null,
        RequestPresetJson: presetJson,
        Enabled: true,
        IsDefault: true);

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenEndpointMissing()
    {
        var extractor = CreateExtractor(new AzureDocumentIntelligenceOptions { Endpoint = string.Empty, ApiKey = "key" });

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", ModeWithPreset(null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Endpoint and AzureDocumentIntelligence:ApiKey are required*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenApiKeyMissing()
    {
        var extractor = CreateExtractor(new AzureDocumentIntelligenceOptions { Endpoint = "https://di.example.com", ApiKey = string.Empty });

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", ModeWithPreset(null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Endpoint and AzureDocumentIntelligence:ApiKey are required*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenEndpointNotAbsolute()
    {
        var extractor = CreateExtractor(new AzureDocumentIntelligenceOptions { Endpoint = "not-a-url", ApiKey = "key" });

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(content, "doc.pdf", ModeWithPreset(null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Endpoint must be an absolute URI*");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_Throws_WhenPresetApiVersionUnsupported()
    {
        var extractor = CreateExtractor(new AzureDocumentIntelligenceOptions { Endpoint = "https://di.example.com", ApiKey = "key" });

        await using var content = new MemoryStream(new byte[] { 1 });
        var act = async () => await extractor.ExtractMarkdownAsync(
            content,
            "doc.pdf",
            ModeWithPreset("{\"ApiVersion\":\"2099-01-01\"}"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ApiVersion '2099-01-01' is not supported*");
    }

    [TestMethod]
    public void Provider_ReturnsAzureProviderId()
    {
        var extractor = CreateExtractor(new AzureDocumentIntelligenceOptions { Endpoint = "https://di.example.com", ApiKey = "key" });
        extractor.Provider.Should().Be(ServiceProviderIds.DocumentIntelligenceAzure);
    }

    private static AzureDocumentIntelligenceExtractor CreateExtractor(AzureDocumentIntelligenceOptions options) =>
        new(
            new StaticOptionsMonitor<AzureDocumentIntelligenceOptions>(options),
            new StaticOptionsMonitor<DocumentIntelligenceOptions>(new DocumentIntelligenceOptions { TimeoutSeconds = 30 }),
            NullLogger<AzureDocumentIntelligenceExtractor>.Instance);
}
