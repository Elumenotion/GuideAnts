using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalServiceModelRefRulesTests
{
    [TestMethod]
    [DataRow(".cache", false)]
    [DataRow("qwen3_embedding_0_6b", true)]
    [DataRow("harrier-oss-v1-0.6b", true)]
    [DataRow("", false)]
    [DataRow("  ", false)]
    public void IsLoadableLocalModelRef_matches_python_model_path_rules(string modelRef, bool expected)
    {
        LocalServiceModelRefRules.IsLoadableLocalModelRef(modelRef).Should().Be(expected);
    }
}
