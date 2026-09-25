using FluentAssertions;
using GuideAntsApi.Endpoints.Settings;
using GuideAntsApi.Models.Settings;

namespace GuideAntsApi.Tests.Endpoints;

[TestClass]
public sealed class SettingsModelOnboardingSupportTests
{
    [TestMethod]
    public void BuildCloudModelCreateRequest_CarriesContextWindowValuesFromCatalog()
    {
        var request = new AddModelRequest(
            "openai-chat",
            new AddModelCatalogDto("m", "M", null, null, true, ContextWindowTokens: 271828, MaxOutputTokens: 31415),
            null,
            null);

        var create = SettingsModelOnboardingSupport.BuildCloudModelCreateRequest(request);

        create.ContextWindowTokens.Should().Be(271828);
        create.MaxOutputTokens.Should().Be(31415);
    }

    [TestMethod]
    public void BuildCloudModelCreateRequest_LeavesContextWindowNullWhenAbsent()
    {
        var request = new AddModelRequest(
            "openai-chat",
            new AddModelCatalogDto("m", "M", null, null, true),
            null,
            null);

        var create = SettingsModelOnboardingSupport.BuildCloudModelCreateRequest(request);

        create.ContextWindowTokens.Should().BeNull();
        create.MaxOutputTokens.Should().BeNull();
    }
}
