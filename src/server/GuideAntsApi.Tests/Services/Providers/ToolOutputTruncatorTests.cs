using System.Text.Json;
using AntRunner.Chat;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;

namespace GuideAntsApi.Tests.Services.Providers;

[TestClass]
public sealed class ToolOutputTruncatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void Truncate_NullOutput_ReturnsNotTruncated()
    {
        var result = ToolOutputTruncator.Truncate(null);
        result.WasTruncated.Should().BeFalse();
        result.Output.Should().BeNull();
    }

    [TestMethod]
    public void Truncate_SmallOutput_ReturnsOriginal()
    {
        const string output = "{\"standardOutput\":\"hello\"}";
        var result = ToolOutputTruncator.Truncate(output);
        result.WasTruncated.Should().BeFalse();
        result.Output.Should().Be(output);
    }

    [TestMethod]
    public void Truncate_OutputAtLimit_ReturnsOriginal()
    {
        var output = new string('x', ToolOutputTruncator.MaxCharacters);
        var result = ToolOutputTruncator.Truncate(output);
        result.WasTruncated.Should().BeFalse();
        result.Output.Should().Be(output);
    }

    [TestMethod]
    public void Truncate_LargeScriptExecutionResult_TruncatesStdoutAndAddsStderrNotice()
    {
        var largeStdout = new string('x', 80_000);
        var original = new ScriptExecutionResult
        {
            StandardOutput = largeStdout,
            StandardError = "existing stderr"
        };
        var serialized = JsonSerializer.Serialize(original, JsonOptions);

        var result = ToolOutputTruncator.Truncate(serialized);

        result.WasTruncated.Should().BeTrue();
        result.OriginalLength.Should().Be(serialized.Length);
        result.Output.Should().NotBeNull();
        result.Output!.Length.Should().BeLessThanOrEqualTo(ToolOutputTruncator.MaxCharacters);

        using var doc = JsonDocument.Parse(result.Output);
        var stderr = doc.RootElement.GetProperty("standardError").GetString();
        stderr.Should().Contain("Response truncated for length");
        stderr.Should().Contain("existing stderr");

        var stdout = doc.RootElement.GetProperty("standardOutput").GetString();
        stdout.Should().Contain("[... output truncated for length ...]");
        stdout!.Length.Should().BeLessThan(largeStdout.Length);
    }

    [TestMethod]
    public void Truncate_LargeScriptExecutionResult_PreservesFileLists()
    {
        var original = new ScriptExecutionResult
        {
            StandardOutput = new string('x', 80_000),
            NewFiles = new List<string> { "a.txt", "b.txt" },
            ModifiedFiles = new List<string> { "c.txt" }
        };
        var serialized = JsonSerializer.Serialize(original, JsonOptions);

        var result = ToolOutputTruncator.Truncate(serialized);

        result.WasTruncated.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetProperty("newFiles").EnumerateArray().Should().HaveCount(2);
        doc.RootElement.GetProperty("modifiedFiles").EnumerateArray().Should().HaveCount(1);
    }

    [TestMethod]
    public void Truncate_LargePlainOutput_WrapsInScriptExecutionEnvelope()
    {
        var largeOutput = new string('y', 80_000);

        var result = ToolOutputTruncator.Truncate(largeOutput);

        result.WasTruncated.Should().BeTrue();
        result.Output!.Length.Should().BeLessThanOrEqualTo(ToolOutputTruncator.MaxCharacters);

        using var doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("standardError").GetString().Should().Contain("Response truncated for length");
        doc.RootElement.GetProperty("standardOutput").GetString().Should().Contain("[... output truncated for length ...]");
    }
}
