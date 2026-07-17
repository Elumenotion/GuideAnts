using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.IntegrationTests.Settings;

[TestClass]
public sealed class ImageBundleDefinitionIntegrationTests : BaseEndpointTest
{
    [ClassInitialize]
    public static Task ClassInitialize(TestContext context) => InitializeSharedFactoryAsync(context);

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestMethod]
    public async Task GetImageGenerationBundleDefinitions_ReturnsCheckedInDefaults()
    {
        var response = await Client.GetAsync("/api/settings/services/ImageGeneration/bundle-definitions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ImageGenerationBundleDefinitionListDto>();
        payload.Should().NotBeNull();
        payload!.Items.Should().NotBeEmpty();
        payload.Items.Should().Contain(x => x.BundleId == "flux2-klein-4b");
        payload.Items.Should().Contain(x => x.BundleId == "flux2-klein-9b");
        payload.Items.Should().Contain(x => x.BundleId == "FLUX.2-dev");

        foreach (var item in payload.Items)
        {
            item.Sampling.Steps.Should().BeGreaterThan(0);
            item.Sampling.CfgScale.Should().BeGreaterThan(0);
            item.Sampling.SamplingMethod.Should().NotBeNullOrWhiteSpace();
            item.Roles.Diffusion.Repo.Should().NotBeNullOrWhiteSpace();
            item.Roles.Diffusion.File.Should().NotBeNullOrWhiteSpace();
            item.Roles.Vae.Repo.Should().NotBeNullOrWhiteSpace();
            item.Roles.Vae.File.Should().NotBeNullOrWhiteSpace();
            item.Roles.TextEncoder.Repo.Should().NotBeNullOrWhiteSpace();
            item.Roles.TextEncoder.File.Should().NotBeNullOrWhiteSpace();
        }
    }

    [TestMethod]
    public async Task ExportImageGenerationBundleDefinition_ReturnsCanonicalJson()
    {
        var response = await Client.GetAsync("/api/settings/services/ImageGeneration/bundle-definitions/flux2-klein-4b/export");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var definition = await response.Content.ReadFromJsonAsync<ImageGenerationBundleDefinitionDto>();
        definition.Should().NotBeNull();
        definition!.BundleId.Should().Be("flux2-klein-4b");
        definition.Sampling.Steps.Should().Be(4);
        definition.Sampling.CfgScale.Should().Be(1.0);
        definition.Sampling.SamplingMethod.Should().Be("euler");
    }
}
