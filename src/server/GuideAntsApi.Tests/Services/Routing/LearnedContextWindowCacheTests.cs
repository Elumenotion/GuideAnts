using FluentAssertions;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Routing;

[TestClass]
public sealed class LearnedContextWindowCacheTests
{
    [TestMethod]
    public void Get_ReturnsNull_WhenNothingLearned()
    {
        var cache = new LearnedContextWindowCache();
        cache.Get("unseen-model").Should().BeNull();
    }

    [TestMethod]
    public void Record_ThenGet_ReturnsLearnedValue()
    {
        var cache = new LearnedContextWindowCache();
        cache.Record("some-model", 8192);
        cache.Get("some-model").Should().Be(8192);
    }

    [TestMethod]
    public void Record_IgnoresNullAndNonPositiveValues()
    {
        var cache = new LearnedContextWindowCache();
        cache.Record("m", null);
        cache.Record("m", 0);
        cache.Record("m", -1);
        cache.Get("m").Should().BeNull();
    }

    [TestMethod]
    public void Record_KeepsSmallestObservedWindow()
    {
        // A larger later value may come from a different deployment of the same id.
        // The smallest observed window is the safe one for a headroom estimate.
        var cache = new LearnedContextWindowCache();
        cache.Record("m", 32_000);
        cache.Record("m", 8_192);
        cache.Record("m", 16_000);
        cache.Get("m").Should().Be(8_192);
    }

    [TestMethod]
    public void Get_IsCaseInsensitiveOnModelId()
    {
        var cache = new LearnedContextWindowCache();
        cache.Record("Some-Model", 4096);
        cache.Get("some-model").Should().Be(4096);
    }
}
