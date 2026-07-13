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
        var assistant = new AssistantDefinition { MaxToolCallsPerTurn = null, MaxToolRoundsPerTurn = null };
        var state = ToolLimitState.FromAssistantDefinition(assistant);

        state.MaxToolCalls.Should().BeNull();
        state.MaxToolRounds.Should().BeNull();
        state.ToolCallsUsed.Should().Be(0);
        state.Phase.Should().Be(LimitEscalationPhase.None);
    }

    [TestMethod]
    public void WouldExceedToolCalls_AllowsUpToConfiguredMax()
    {
        var state = new ToolLimitState(12, null, 11, 0, LimitEscalationPhase.None);

        state.WouldExceedToolCalls(1).Should().BeFalse();
        state.WouldExceedToolCalls(2).Should().BeTrue();
    }

    [TestMethod]
    public void WouldExceedToolCalls_NullMax_NeverExceeds()
    {
        var state = new ToolLimitState(null, null, 100, 0, LimitEscalationPhase.None);
        state.WouldExceedToolCalls(50).Should().BeFalse();
    }

    [TestMethod]
    public void ForNestedInvoke_CapsChildBudgetByParentRemainingAfterInvoke()
    {
        var parent = new ToolLimitState(5, null, 3, 0, LimitEscalationPhase.None);
        var child = ToolLimitState.ForNestedInvoke(parent, childMaxToolCalls: 20, childMaxToolRounds: null, memberOverride: null);

        child.MaxToolCalls.Should().Be(1);
    }

    [TestMethod]
    public void ForNestedInvoke_UsesChildAssistantLimitWhenParentUnlimited()
    {
        var parent = new ToolLimitState(null, null, 3, 0, LimitEscalationPhase.None);
        var child = ToolLimitState.ForNestedInvoke(parent, childMaxToolCalls: 8, childMaxToolRounds: 2, memberOverride: null);

        child.MaxToolCalls.Should().Be(8);
        child.MaxToolRounds.Should().Be(2);
        child.ToolCallsUsed.Should().Be(0);
    }

    [TestMethod]
    public void ForNestedInvoke_MemberOverrideWinsOverChildAssistantLimit()
    {
        var parent = new ToolLimitState(null, null, 0, 0, LimitEscalationPhase.None);
        var child = ToolLimitState.ForNestedInvoke(parent, childMaxToolCalls: 20, childMaxToolRounds: null, memberOverride: 6);

        child.MaxToolCalls.Should().Be(6);
    }

    [TestMethod]
    public void HasExceededToolRounds_UsesConfiguredMax()
    {
        var state = new ToolLimitState(null, 3, 0, 4, LimitEscalationPhase.None);
        state.HasExceededToolRounds().Should().BeTrue();
    }

    [TestMethod]
    public void WouldExceedToolCalls_12thExecutes_13thSynthetic()
    {
        var state = new ToolLimitState(12, null, 11, 0, LimitEscalationPhase.None);

        state.WouldExceedToolCalls(1).Should().BeFalse();
        state = state.AddToolCalls(1);
        state.WouldExceedToolCalls(1).Should().BeTrue();
    }

    [TestMethod]
    public void BuildLimitToolResultMessage_IncludesConfiguredMaxAndUsedCount()
    {
        var state = new ToolLimitState(12, null, 12, 0, LimitEscalationPhase.SoftBlocked);
        state.BuildLimitToolResultMessage().Should().Contain("12/12");
    }
}
