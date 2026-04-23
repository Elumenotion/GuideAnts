using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class LlamaModelStorePathResolverTests
{
    [TestMethod]
    public void ResolveHostPath_StripsModelsLocalLlamaPrefix()
    {
        var root = @"D:\store";
        var host = LlamaModelStorePathResolver.ResolveHostPath(root, "/models-local/llama/MyAlias/file.gguf");
        host.Should().EndWith(Path.Combine("store", "MyAlias", "file.gguf"));
    }

    [TestMethod]
    public void ResolveHostPath_TreatsBareRootAsStoreRoot()
    {
        var root = @"D:\store";
        var host = LlamaModelStorePathResolver.ResolveHostPath(root, "/models-local/llama");
        host.Should().EndWith("store");
    }

    [TestMethod]
    public void ResolveHostPath_TreatsUnknownAbsolutePathAsStoreRelative()
    {
        var root = @"D:\store";
        var host = LlamaModelStorePathResolver.ResolveHostPath(root, "/SomethingElse/file.gguf");
        // Unknown prefix — resolver falls back to "relative to store" so a
        // mis-routed path still lands under the configured root instead
        // of escaping to an arbitrary host location.
        host.Should().EndWith(Path.Combine("store", "SomethingElse", "file.gguf"));
    }

    [TestMethod]
    public void ToContainerPath_BuildsModelsLocalLlamaPath()
    {
        LlamaModelStorePathResolver.ToContainerPath("MyAlias/file.gguf")
            .Should().Be("/models-local/llama/MyAlias/file.gguf");
    }
}
