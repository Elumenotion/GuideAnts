using FluentAssertions;
using GuideAnts.Usage;

namespace GuideAntsApi.Tests.Usage;

[TestClass]
public sealed class ChatCostCalculatorTests
{
    [TestMethod]
    public void CalculateCost_Returns_zero_when_model_missing()
    {
        var metrics = new UsageMetrics(ValueInput: 1000, ValueOutput: 500);

        ChatCostCalculator.CalculateCost(metrics, null).Should().Be(0m);
        ChatCostCalculator.CalculateCost(metrics, "   ").Should().Be(0m);
    }

    [TestMethod]
    public void CalculateCost_Uses_gpt_5_mini_pricing()
    {
        var metrics = new UsageMetrics(
            ValueInput: 1_000_000,
            ValueCachedInput: 1_000_000,
            ValueReasoning: 1_000_000,
            ValueOutput: 1_000_000);

        var cost = ChatCostCalculator.CalculateCost(metrics, "gpt-5-mini");

        cost.Should().Be(4.025m);
    }

    [TestMethod]
    public void CalculateCost_Bills_cached_tokens_once_for_partial_cache_hit()
    {
        var metrics = new UsageMetrics(
            ValueInput: 1_000,
            ValueCachedInput: 800,
            ValueOutput: 100);

        var cost = ChatCostCalculator.CalculateCost(metrics, "gpt-5-mini");

        // 200 non-cached @ $0.25/1M + 800 cached @ $0.025/1M + 100 output @ $2/1M
        cost.Should().Be(0.00027m);
    }

    [TestMethod]
    public void CalculateCost_Uses_claude_prefix_pricing()
    {
        var metrics = new UsageMetrics(ValueInput: 1_000_000, ValueOutput: 1_000_000);

        var sonnetCost = ChatCostCalculator.CalculateCost(metrics, "claude-sonnet-4-5-20250929");
        sonnetCost.Should().Be(18m);

        var haikuCost = ChatCostCalculator.CalculateCost(metrics, "claude-haiku-4-5-20251001");
        haikuCost.Should().Be(6m);
    }

    [TestMethod]
    public void CalculateCost_Covers_documented_model_variants()
    {
        var metrics = new UsageMetrics(ValueInput: 1_000_000, ValueOutput: 1_000_000);
        var models = new[]
        {
            "gpt-5.1", "gpt-5", "gpt-5-chat", "gpt-5-pro", "gpt-5.2-codex",
            "gpt-4.1-mini", "gpt-4.1-nano", "claude-opus-4-5-20251101"
        };

        foreach (var model in models)
        {
            ChatCostCalculator.CalculateCost(metrics, model).Should().BeGreaterThan(0m);
        }
    }

    [TestMethod]
    public void CalculateCost_Falls_back_to_gpt_4_1_for_unknown_models()
    {
        var metrics = new UsageMetrics(ValueInput: 1_000_000, ValueOutput: 1_000_000);

        var cost = ChatCostCalculator.CalculateCost(metrics, "unknown-model");

        cost.Should().Be(10m);
    }
}
