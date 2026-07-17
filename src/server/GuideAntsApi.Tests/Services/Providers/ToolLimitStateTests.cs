using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class ToolLimitStateTests
{
    [TestMethod]
    public void FromAssistantDefinition_NullLimits_ProducesUnlimitedState()
    {
        var assistant = new AssistantDefinition { MaxToolCallsPerTurn = null };
        var state = ToolLimitState.FromAssistantDefinition(assistant);

        state.MaxToolCalls.Should().BeNull();
        state.ToolCallsUsed.Should().Be(0);
        state.Phase.Should().Be(LimitEscalationPhase.None);
    }

    [TestMethod]
    public void WouldExceedToolCalls_AllowsUpToConfiguredMax()
    {
        var state = new ToolLimitState(12, 11, LimitEscalationPhase.None);

        state.WouldExceedToolCalls(1).Should().BeFalse();
        state.WouldExceedToolCalls(2).Should().BeTrue();
    }

    [TestMethod]
    public void WouldExceedToolCalls_NullMax_NeverExceeds()
    {
        var state = new ToolLimitState(null, 100, LimitEscalationPhase.None);
        state.WouldExceedToolCalls(50).Should().BeFalse();
    }

    [TestMethod]
    public void ForNestedInvoke_CapsChildBudgetByParentRemainingAfterInvoke()
    {
        var parent = new ToolLimitState(5, 3, LimitEscalationPhase.None);
        var child = ToolLimitState.ForNestedInvoke(parent, childMaxToolCalls: 20, memberOverride: null);

        child.MaxToolCalls.Should().Be(1);
    }

    [TestMethod]
    public void ForNestedInvoke_UsesChildAssistantLimitWhenParentUnlimited()
    {
        var parent = new ToolLimitState(null, 3, LimitEscalationPhase.None);
        var child = ToolLimitState.ForNestedInvoke(parent, childMaxToolCalls: 8, memberOverride: null);

        child.MaxToolCalls.Should().Be(8);
        child.ToolCallsUsed.Should().Be(0);
    }

    [TestMethod]
    public void ForNestedInvoke_MemberOverrideWinsOverChildAssistantLimit()
    {
        var parent = new ToolLimitState(null, 0, LimitEscalationPhase.None);
        var child = ToolLimitState.ForNestedInvoke(parent, childMaxToolCalls: 20, memberOverride: 6);

        child.MaxToolCalls.Should().Be(6);
    }

    [TestMethod]
    public void EvaluateLimitHit_IgnoresLegacyRoundConfig()
    {
        var state = new ToolLimitState(null, 0, LimitEscalationPhase.None);
        state.EvaluateLimitHit(1).Should().Be(ToolLimitHitKind.None);
    }

    [TestMethod]
    public void WouldExceedToolCalls_12thExecutes_13thSynthetic()
    {
        var state = new ToolLimitState(12, 11, LimitEscalationPhase.None);

        state.WouldExceedToolCalls(1).Should().BeFalse();
        state = state.AddToolCalls(1);
        state.WouldExceedToolCalls(1).Should().BeTrue();
    }

    [TestMethod]
    public void EvaluateLimitHit_ReportsToolCallsOnly()
    {
        var state = new ToolLimitState(5, 5, LimitEscalationPhase.None);
        state.EvaluateLimitHit(1).Should().Be(ToolLimitHitKind.ToolCalls);
    }

    [TestMethod]
    public void BuildLimitToolResultMessage_CallLimit_UsesToolCounters()
    {
        var state = new ToolLimitState(5, 5, LimitEscalationPhase.None);
        state.BuildLimitToolResultMessage(1)
            .Should()
            .Contain("tools 6/5")
            .And.NotContain("tool-use cycles");
    }

    [TestMethod]
    public void BuildRuntimeOverrideSystemMessage_UsesStoredHitKind()
    {
        var state = new ToolLimitState(10, 5, LimitEscalationPhase.SoftBlocked, ToolLimitHitKind.ToolCalls);
        state.BuildRuntimeOverrideSystemMessage()
            .Should()
            .Contain("Tool execution limit was reached");
    }

    [TestMethod]
    public void FormatLimitHitKinds_SerializesCallLimitHit()
    {
        ToolLimitState.FormatLimitHitKinds(ToolLimitHitKind.ToolCalls)
            .Should()
            .Be("ToolCalls");
    }
}
