using FluentAssertions;
using GuideAntsApi.BackgroundJobs.Options;
using GuideAntsApi.BackgroundJobs.Services;
using GuideAntsApi.Services.Routing;
using GuideAntsApi.Tests.TestUtils;
using Microsoft.Extensions.Logging;
using Moq;

namespace GuideAntsApi.Tests.BackgroundJobs;

[TestClass]
public sealed class ProviderRoutedDocumentIntelligenceServiceTests
{
    private const string AzureProviderSection = "AzureDocumentIntelligence";
    private const string LocalProviderSection = "LocalServiceHosts:DocumentIntelligenceBaseUrl";

    [TestMethod]
    public async Task ExtractMarkdownAsync_UsesAzureExtractor_WhenModeSelectsAzureProvider()
    {
        var service = CreateService(
            providerSection: AzureProviderSection,
            [
                new FakeExtractor(ServiceProviderIds.DocumentIntelligenceAzure, "azure-markdown"),
                new FakeExtractor(ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp, "docling-markdown")
            ]);

        using var stream = new MemoryStream("test"u8.ToArray());
        var result = await service.ExtractMarkdownAsync(stream, "sample.pdf", CancellationToken.None);

        result.Should().Be("azure-markdown");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_UsesDoclingExtractor_WhenModeSelectsLocalProvider()
    {
        var service = CreateService(
            providerSection: LocalProviderSection,
            [
                new FakeExtractor(ServiceProviderIds.DocumentIntelligenceAzure, "azure-markdown"),
                new FakeExtractor(ServiceProviderIds.DocumentIntelligenceLocalDoclingHttp, "docling-markdown")
            ]);

        using var stream = new MemoryStream("test"u8.ToArray());
        var result = await service.ExtractMarkdownAsync(stream, "sample.pdf", CancellationToken.None);

        result.Should().Be("docling-markdown");
    }

    [TestMethod]
    public async Task ExtractMarkdownAsync_ThrowsRoutingException_WhenModeReferencesUnsupportedProviderSection()
    {
        var service = CreateService(
            providerSection: "SomeBogusSection",
            [
                new FakeExtractor(ServiceProviderIds.DocumentIntelligenceAzure, "azure-markdown")
            ]);

        using var stream = new MemoryStream("test"u8.ToArray());
        var act = async () => await service.ExtractMarkdownAsync(stream, "sample.pdf", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<RoutingException>();
        ex.Which.Code.Should().Be(RoutingErrorCodes.ProviderNotReady);
        ex.Which.ProviderSection.Should().Be("SomeBogusSection");
    }

    private static ProviderRoutedDocumentIntelligenceService CreateService(
        string providerSection,
        IEnumerable<IDocumentIntelligenceExtractor> extractors)
    {
        var resolver = new FakeServiceModeResolver(
            RoutedServiceNames.DocumentIntelligence,
            providerSection: providerSection);

        var extractionOptions = Microsoft.Extensions.Options.Options.Create(new MarkdownExtractionOptions
        {
            MaxFileSizeMB = 500,
            SupportedExtensions = []
        });

        return new ProviderRoutedDocumentIntelligenceService(
            extractors,
            resolver,
            extractionOptions,
            Mock.Of<ILogger<ProviderRoutedDocumentIntelligenceService>>());
    }

    private sealed class FakeExtractor(string provider, string response) : IDocumentIntelligenceExtractor
    {
        public string Provider { get; } = provider;

        public Task<string> ExtractMarkdownAsync(
            Stream content,
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(response);
    }
}
