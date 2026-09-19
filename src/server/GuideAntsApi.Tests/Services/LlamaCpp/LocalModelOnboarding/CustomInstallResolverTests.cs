using FluentAssertions;
using GuideAntsApi.Models.Settings;
using GuideAntsApi.Services.HuggingFace;
using GuideAntsApi.Services.LlamaCpp;
using GuideAntsApi.Services.LlamaCpp.LocalModelOnboarding;
using Moq;

namespace GuideAntsApi.Tests.Services.LlamaCpp.LocalModelOnboarding;

[TestClass]
public sealed class CustomInstallResolverTests
{
    private static CustomInstallResolver CreateResolver(string? token = "hf_token")
    {
        var tokenResolver = new Mock<IHuggingFaceTokenResolver>();
        tokenResolver.Setup(x => x.Resolve()).Returns(token);
        return new CustomInstallResolver(tokenResolver.Object, new Mock<ILlamaRuntimeAdminClient>().Object);
    }

    private static AddModelRequest CreateRequest(
        IReadOnlyDictionary<string, string>? routerPreset,
        string? resolvedRevision = "abc123")
    {
        return new AddModelRequest(
            Provider: "llama-cpp",
            Catalog: new AddModelCatalogDto(
                ModelId: "qwen-local",
                DisplayName: "Qwen Local",
                Description: "",
                DisplayOrder: null,
                IsActive: true),
            ProviderConfig: null,
            Install: new AddModelInstallDto(
                Source: LocalModelInstallSources.HuggingFace,
                RouterModelId: "qwen-local",
                HuggingFace: new AddModelInstallHuggingFaceDto(
                    Repository: "unsloth/Qwen3.6-9B-GGUF",
                    QuantIncludePattern: "",
                    MmprojIncludePattern: "",
                    TargetDirectory: "qwen-local",
                    ResolvedRevision: resolvedRevision,
                    ModelFiles: ["Qwen3.6-9B-Q5_K_M.gguf"],
                    MmprojFiles: [],
                    RouterPreset: routerPreset)));
    }

    [TestMethod]
    public async Task ResolveAsync_EmptyPreset_ResolvesWithEmptyRouterPreset()
    {
        var resolver = CreateResolver();
        var request = CreateRequest(new Dictionary<string, string>());
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var input = await resolver.ResolveAsync(request, command, CancellationToken.None);

        input.RouterPreset.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ResolveAsync_NullPreset_ResolvesWithEmptyRouterPreset()
    {
        var resolver = CreateResolver();
        var request = CreateRequest(routerPreset: null);
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var input = await resolver.ResolveAsync(request, command, CancellationToken.None);

        input.RouterPreset.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ResolveAsync_NonEmptyPreset_StillValidatesKeys()
    {
        var resolver = CreateResolver();
        var request = CreateRequest(new Dictionary<string, string> { ["model"] = "x.gguf" });
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);

        await act.Should().ThrowAsync<AddModelException>()
            .WithMessage("*infrastructure key*");
    }

    [TestMethod]
    public async Task ResolveAsync_MissingModelFiles_ThrowsInstallStepFailed()
    {
        var resolver = CreateResolver();
        var request = new AddModelRequest(
            Provider: "llama-cpp",
            Catalog: new AddModelCatalogDto("qwen-local", "Qwen Local", "", null, true),
            ProviderConfig: null,
            Install: new AddModelInstallDto(
                Source: LocalModelInstallSources.HuggingFace,
                RouterModelId: "qwen-local",
                HuggingFace: new AddModelInstallHuggingFaceDto(
                    Repository: "unsloth/Qwen3.6-9B-GGUF",
                    QuantIncludePattern: "",
                    MmprojIncludePattern: "",
                    TargetDirectory: "qwen-local",
                    ResolvedRevision: "abc123",
                    ModelFiles: [],
                    MmprojFiles: [],
                    RouterPreset: new Dictionary<string, string>())));
        var command = LocalModelOnboardingCommand.FromAddModelRequest(request);

        var act = async () => await resolver.ResolveAsync(request, command, CancellationToken.None);

        // No model files means the command is not an explicit custom install,
        // so the resolver has no explicit input to resolve.
        await act.Should().ThrowAsync<AddModelException>()
            .WithMessage("*Explicit Hugging Face artifact input is required*");
    }
}
