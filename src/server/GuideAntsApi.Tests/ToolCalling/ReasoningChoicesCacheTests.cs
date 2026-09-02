using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
[DoNotParallelize]
public sealed class ReasoningChoicesCacheTests
{
    [TestInitialize]
    public void SetUp() => ReasoningChoicesCache.Clear();

    [TestCleanup]
    public void TearDown() => ReasoningChoicesCache.Clear();

    [TestMethod]
    public void TryGet_MissingModel_ReturnsFalse()
    {
        ReasoningChoicesCache.TryGet("gpt-x", DateTime.UtcNow, out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryGet_FreshEntry_ReturnsCachedJson_IncludingNull()
    {
        var now = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        ReasoningChoicesCache.Set("gpt-x", "[\"low\",\"high\"]", now);
        ReasoningChoicesCache.Set("gpt-y", null, now);

        ReasoningChoicesCache.TryGet("gpt-x", now.AddSeconds(59), out var json).Should().BeTrue();
        json.Should().Be("[\"low\",\"high\"]");

        ReasoningChoicesCache.TryGet("gpt-y", now.AddSeconds(59), out var nullJson).Should().BeTrue();
        nullJson.Should().BeNull();
    }

    [TestMethod]
    public void TryGet_ExpiredEntry_ReturnsFalse()
    {
        var now = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        ReasoningChoicesCache.Set("gpt-x", "[]", now);

        ReasoningChoicesCache.TryGet("gpt-x", now.AddSeconds(60), out _).Should().BeFalse();
    }
}
