using FluentAssertions;
using GuideAntsApi.Configuration;

namespace GuideAntsApi.Tests.Configuration;

[TestClass]
public sealed class RuntimeConfigurationPlaceholdersTests
{
    [TestMethod]
    public void IsDiscardLoopbackUrl_LoopbackPort9_IsTrue()
    {
        RuntimeConfigurationPlaceholders.IsDiscardLoopbackUrl("http://127.0.0.1:9").Should().BeTrue();
        RuntimeConfigurationPlaceholders.IsDiscardLoopbackUrl("http://127.0.0.1:9/llama-cpp").Should().BeTrue();
        RuntimeConfigurationPlaceholders.IsDiscardLoopbackUrl("http://localhost:9").Should().BeTrue();
    }

    [TestMethod]
    public void HasUsableUrl_LoopbackPort9_IsFalse()
    {
        RuntimeConfigurationPlaceholders.HasUsableUrl("http://127.0.0.1:9/llama-cpp").Should().BeFalse();
        RuntimeConfigurationPlaceholders.HasUsableUrl("http://localhost:9").Should().BeFalse();
    }

    [TestMethod]
    public void HasUsableUrl_RealStackHost_IsTrue()
    {
        RuntimeConfigurationPlaceholders.HasUsableUrl("http://guideants-ai:80/llama-cpp").Should().BeTrue();
        RuntimeConfigurationPlaceholders.HasUsableUrl("http://127.0.0.1:8110/llama-cpp").Should().BeTrue();
    }
}
