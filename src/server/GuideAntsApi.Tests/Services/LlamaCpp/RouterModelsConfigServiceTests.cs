using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class RouterModelsConfigServiceTests
{
    [TestMethod]
    public void ParseIni_ParsesVersionAndSections()
    {
        var ini = """
            version = 1

            [Alias-One]
            model = /models-local/llama/A/x.gguf
            mmproj = /models-local/llama/A/m.gguf

            [Alias-Two]
            model = /models-local/llama/B/b.gguf
            mmproj = /models-local/llama/B/mm.gguf
            """;

        var entries = RouterModelsConfigService.ParseIni(ini);

        entries.Should().HaveCount(2);
        entries[0].Alias.Should().Be("Alias-One");
        entries[0].ModelPath.Should().Be("/models-local/llama/A/x.gguf");
        entries[0].MmprojPath.Should().Be("/models-local/llama/A/m.gguf");
        entries[1].Alias.Should().Be("Alias-Two");
    }
}
