using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;
using GuideAntsApi.Services.LlamaCpp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiStackHostUrlsTests
{
    [TestMethod]
    public void NormalizeStackBaseUrl_DiscardLoopback_ReturnsNull()
    {
        LocalAiStackHostUrls.NormalizeStackBaseUrl("http://127.0.0.1:9/llama-cpp").Should().BeNull();
        LocalAiStackHostUrls.NormalizeStackBaseUrl("http://localhost:9").Should().BeNull();
    }

    [TestMethod]
    public void TryDeriveAdminBaseUriFromLlamaCppUrl_DiscardLoopback_ReturnsNull()
    {
        LocalAiStackHostUrls.TryDeriveAdminBaseUriFromLlamaCppUrl("http://127.0.0.1:9/llama-cpp")
            .Should().BeNull();
    }

    [TestMethod]
    public void DeriveAdminBaseUriFromLlamaCppUrl_DiscardLoopback_Throws()
    {
        var derive = () => LocalAiStackHostUrls.DeriveAdminBaseUriFromLlamaCppUrl(
            "http://127.0.0.1:9/llama-cpp");

        derive.Should().Throw<ArgumentException>().WithParameterName("llamaCppBaseUrl");
    }

    [TestMethod]
    public void TryDeriveAdminBaseUriFromLlamaCppUrl_UsableUrl_DerivesLlamaAdmin()
    {
        var uri = LocalAiStackHostUrls.TryDeriveAdminBaseUriFromLlamaCppUrl("http://guideants-ai:80/llama-cpp");

        uri.Should().NotBeNull();
        uri!.AbsoluteUri.Should().Be("http://guideants-ai/llama-admin/");
    }

    [TestMethod]
    public void ApplyLlamaRuntimeAdminBaseAddress_DiscardLoopback_LeavesBaseAddressUnset()
    {
        using var client = new HttpClient();

        var apply = () => LocalAiStackHostUrls.ApplyLlamaRuntimeAdminBaseAddress(
            client,
            "http://127.0.0.1:9/llama-cpp");

        apply.Should().NotThrow();
        client.BaseAddress.Should().BeNull();
    }

    [TestMethod]
    public void ApplyLlamaRuntimeAdminBaseAddress_UsableUrl_SetsLlamaAdminBaseAddress()
    {
        using var client = new HttpClient();

        LocalAiStackHostUrls.ApplyLlamaRuntimeAdminBaseAddress(client, "http://guideants-ai:80/llama-cpp");

        client.BaseAddress.Should().NotBeNull();
        client.BaseAddress!.AbsoluteUri.Should().Be("http://guideants-ai/llama-admin/");
    }

    [TestMethod]
    public void StackHostResolver_SlimDiscardUrls_HasNoConfiguredStack()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://127.0.0.1:9/llama-cpp",
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://127.0.0.1:9",
                ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://127.0.0.1:9",
                ["LocalServiceHosts:ImageGenerationBaseUrl"] = "http://127.0.0.1:9",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://127.0.0.1:9",
            })
            .Build();

        var resolver = new LocalAiStackHostResolver(configuration);

        resolver.HasAnyConfiguredStack().Should().BeFalse();
        resolver.GetAllConfiguredStackBases().Should().BeEmpty();
    }

    [TestMethod]
    public void LlamaRuntimeAdminClient_DiscardLoopback_CanBeResolvedFromDi()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://127.0.0.1:9/llama-cpp",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient<ILlamaRuntimeAdminClient, LlamaRuntimeAdminClient>(client =>
        {
            var baseUrl = configuration["LlamaCpp:BaseUrl"]
                ?? throw new InvalidOperationException("LlamaCpp:BaseUrl is required.");
            LocalAiStackHostUrls.ApplyLlamaRuntimeAdminBaseAddress(client, baseUrl);
        });

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<ILlamaRuntimeAdminClient>();

        resolve.Should().NotThrow();
    }
}
