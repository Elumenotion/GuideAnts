using FluentAssertions;
using GuideAntsApi.Services.Bootstrap;
using Microsoft.Extensions.Configuration;

namespace GuideAntsApi.Tests.Services.Bootstrap;

[TestClass]
public sealed class LocalAiWarmupPlanSplitterTests
{
    [TestMethod]
    public void Split_SameStack_ProducesSinglePlanWithAllServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://guideants-ai:80/llama-cpp",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://guideants-ai:80",
            })
            .Build();

        var resolver = new LocalAiStackHostResolver(configuration);
        var splitter = new LocalAiWarmupPlanSplitter(resolver);
        var planJson =
            "{\"schemaVersion\":1,\"services\":{"
            + "\"llama\":{\"enabled\":true,\"routerAlias\":\"qwen\"},"
            + "\"SpeechTranscription\":{\"enabled\":false},"
            + "\"Embeddings\":{\"enabled\":true,\"modelPath\":\"emb-model\"},"
            + "\"SpeechSynthesis\":{\"enabled\":false},"
            + "\"ImageGeneration\":{\"enabled\":false}"
            + "}}";

        var stacks = splitter.Split(planJson);

        stacks.Should().HaveCount(1);
        stacks[0].StackBaseUrl.Should().Be("http://guideants-ai");
        stacks[0].PlanJson.Should().Contain("\"routerAlias\":\"qwen\"");
        stacks[0].PlanJson.Should().Contain("\"modelPath\":\"emb-model\"");
    }

    [TestMethod]
    public void Split_PerInstanceLlamaPlan_PassesEachInstancesSectionThrough()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://local-pc:80/llama-cpp",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://192.0.2.1:8110",
            })
            .Build();

        var resolver = new LocalAiStackHostResolver(configuration);
        var splitter = new LocalAiWarmupPlanSplitter(resolver);
        // The builder emits one section per llama instance, keyed by instance base.
        var planJson = "{\"schemaVersion\":1,\"services\":{\"llama.local-pc\":{\"enabled\":true,\"routerAlias\":\"qwen-local\"},\"llama.192.0.2.1:8112\":{\"enabled\":true,\"routerAlias\":\"qwen-max\"},\"SpeechTranscription\":{\"enabled\":false},\"Embeddings\":{\"enabled\":false},\"SpeechSynthesis\":{\"enabled\":false},\"ImageGeneration\":{\"enabled\":false}}}";

        var stacks = splitter.Split(planJson);

        // Config-declared stacks only (no row-owned resolver stub here).
        stacks.Should().HaveCount(2);

        var localStack = stacks.Single(st =>
            st.StackBaseUrl.Contains("local-pc", StringComparison.OrdinalIgnoreCase));
        localStack.PlanJson.Should().Contain("\"routerAlias\":\"qwen-local\"");

        var auxStack = stacks.Single(st =>
            st.StackBaseUrl.Contains("192.0.2.1:8110", StringComparison.OrdinalIgnoreCase));
        // The aux-only stack is not a llama instance: it must not receive the Max alias.
        auxStack.PlanJson.Should().Contain("\"llama\":{\"enabled\":false}");
        auxStack.PlanJson.Should().NotContain("qwen-max");
    }

    [TestMethod]
    public void Split_SplitStacks_IdlesAuxOnLlamaHostAndLoadsOnRemoteHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlamaCpp:BaseUrl"] = "http://local-pc:80/llama-cpp",
                ["LocalServiceHosts:EmbeddingsBaseUrl"] = "http://192.0.2.1:8110",
                ["LocalServiceHosts:SpeechTranscriptionBaseUrl"] = "http://192.0.2.1:8110",
                ["LocalServiceHosts:SpeechSynthesisBaseUrl"] = "http://192.0.2.1:8110",
            })
            .Build();

        var resolver = new LocalAiStackHostResolver(configuration);
        var splitter = new LocalAiWarmupPlanSplitter(resolver);
        var planJson =
            "{\"schemaVersion\":1,\"services\":{"
            + "\"llama\":{\"enabled\":true,\"routerAlias\":\"qwen\"},"
            + "\"SpeechTranscription\":{\"enabled\":true,\"modelPath\":\"asr-model\"},"
            + "\"Embeddings\":{\"enabled\":true,\"modelPath\":\"emb-model\"},"
            + "\"SpeechSynthesis\":{\"enabled\":false},"
            + "\"ImageGeneration\":{\"enabled\":false}"
            + "}}";

        var stacks = splitter.Split(planJson);

        stacks.Should().HaveCount(2);

        var localStack = stacks.Single(s =>
            s.StackBaseUrl.Contains("local-pc", StringComparison.OrdinalIgnoreCase));
        localStack.PlanJson.Should().Contain("\"routerAlias\":\"qwen\"");
        localStack.PlanJson.Should().Contain("\"Embeddings\":{\"enabled\":false");
        localStack.PlanJson.Should().Contain("\"SpeechTranscription\":{\"enabled\":false");

        var remoteStack = stacks.Single(s =>
            s.StackBaseUrl.Contains("192.0.2.1", StringComparison.OrdinalIgnoreCase));
        remoteStack.PlanJson.Should().Contain("\"modelPath\":\"emb-model\"");
        remoteStack.PlanJson.Should().Contain("\"modelPath\":\"asr-model\"");
        remoteStack.PlanJson.Should().Contain("\"llama\":{\"enabled\":false");
    }
}
