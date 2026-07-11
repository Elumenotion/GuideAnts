using FluentAssertions;
using GuideAntsApi.Services.LlamaCpp;

namespace GuideAntsApi.Tests.Services.LlamaCpp;

[TestClass]
public sealed class RuntimeProfileRequestFieldsValidatorTests
{
    [TestMethod]
    public void ValidateAndNormalize_AcceptsParallelToolCalls()
    {
        var result = RuntimeProfileRequestFieldsValidator.ValidateAndNormalize(
            """{"parallel_tool_calls":true}""");

        result.Should().ContainKey("parallel_tool_calls");
        result["parallel_tool_calls"].GetBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void ValidateAndNormalize_AcceptsEmptyObject()
    {
        var result = RuntimeProfileRequestFieldsValidator.ValidateAndNormalize("{}");
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void ValidateAndNormalize_RejectsArrayRoot()
    {
        var act = () => RuntimeProfileRequestFieldsValidator.ValidateAndNormalize("[]");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*JSON object*");
    }

    [TestMethod]
    public void ValidateAndNormalize_RejectsReservedTransportField()
    {
        var act = () => RuntimeProfileRequestFieldsValidator.ValidateAndNormalize(
            """{"tools":true}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*reserved for transport*");
    }

    [TestMethod]
    public void ValidateAndNormalize_RejectsNestedObjectValue()
    {
        var act = () => RuntimeProfileRequestFieldsValidator.ValidateAndNormalize(
            """{"parallel_tool_calls":{"enabled":true}}""");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*primitive*");
    }

    [TestMethod]
    public void ValidateAndNormalize_RejectsInvalidJson()
    {
        var act = () => RuntimeProfileRequestFieldsValidator.ValidateAndNormalize("{not-json");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid JSON*");
    }

    [TestMethod]
    public void NormalizeJsonString_RoundTripsObject()
    {
        var normalized = RuntimeProfileRequestFieldsValidator.NormalizeJsonString(
            """{"parallel_tool_calls":false}""");
        normalized.Should().Be("""{"parallel_tool_calls":false}""");
    }
}
