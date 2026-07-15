using System.Collections;
using System.Reflection;
using AntRunner.Chat;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class AssistantUtilityCacheTests
{
    [TestInitialize]
    public void SetUp()
    {
        AssistantUtility.ClearAllCache();
    }

    [TestCleanup]
    public void TearDown()
    {
        AssistantUtility.ClearAllCache();
    }

    [TestMethod]
    public void ClearCache_RemovesEntrySoNextLoadWouldMissCache()
    {
        SeedCache("Creative Guide", new AssistantDefinition { Name = "Creative Guide", Model = "gpt-4o" });

        AssistantUtility.ClearCache("Creative Guide");

        GetCacheDictionary().Contains("Creative Guide").Should().BeFalse();
    }

    [TestMethod]
    public void ClearAllCache_RemovesAllEntries()
    {
        SeedCache("Creative Guide", new AssistantDefinition { Name = "Creative Guide" });
        SeedCache("Search", new AssistantDefinition { Name = "Search" });

        AssistantUtility.ClearAllCache();

        GetCacheDictionary().Count.Should().Be(0);
    }

    [TestMethod]
    public void CachedAssistant_DoesNotUseTimeBasedExpiry()
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        cacheType.GetProperties().Select(p => p.Name).Should().NotContain("CachedAt");
        cacheType.GetProperties().Select(p => p.Name).Should().NotContain("Updated");
    }

    private static void SeedCache(string assistantName, AssistantDefinition definition)
    {
        var cacheType = typeof(AssistantUtility).GetNestedType("CachedAssistant", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(cacheType, definition)!;
        var cache = GetCacheDictionary();
        cache[assistantName] = entry;
    }

    private static IDictionary GetCacheDictionary()
    {
        return (IDictionary)typeof(AssistantUtility)
            .GetField("AssistantDefinitionCache", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
    }
}
