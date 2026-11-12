using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalServiceModelRefRulesTests
{
    [TestMethod]
    [DataRow(".cache", false)]
    [DataRow("qwen3_embedding_0_6b", true)]
    [DataRow("bge_m3", true)]
    [DataRow("", false)]
    [DataRow("  ", false)]
    [DataRow("x\"}\"injected\":\"1\"", false)]
    [DataRow("../../etc/passwd", false)]
    [DataRow("a b", false)]
    [DataRow("model\nname", false)]
    public void IsLoadableLocalModelRef_matches_python_model_path_rules(string modelRef, bool expected)
    {
        LocalServiceModelRefRules.IsLoadableLocalModelRef(modelRef).Should().Be(expected);
    }
}
