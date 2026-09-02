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

    private static string CreateOversizedOutput(char fillCharacter) =>
        new string(fillCharacter, ToolOutputTruncator.MaxCharacters + 10_000);

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
    public void Truncate_SuccessfulScriptExecutionResult_OmitsStderr()
    {
        var original = new ScriptExecutionResult
        {
            StandardOutput = "SUCCESS! Video created: out.mp4",
            StandardError = CreateOversizedOutput('e'),
            ExitCode = 0,
            NewFiles = new List<string> { "out.mp4" }
        };
        var serialized = JsonSerializer.Serialize(original, JsonOptions);

        var result = ToolOutputTruncator.Truncate(serialized);

        result.WasTruncated.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Output!);
        doc.RootElement.GetProperty("standardError").GetString().Should().BeEmpty();
        doc.RootElement.GetProperty("standardOutput").GetString().Should().Contain("SUCCESS");
        doc.RootElement.GetProperty("newFiles").EnumerateArray().Should().ContainSingle()
            .Which.GetString().Should().Be("out.mp4");
    }

    [TestMethod]
    public void ForToolCall_NonZeroExitCode_PreservesStderr()
    {
        var original = new ScriptExecutionResult
        {
            StandardOutput = string.Empty,
            StandardError = "actual failure",
            ExitCode = 1
        };

        var toolCall = original.ForToolCall();

        toolCall.Should().BeSameAs(original);
        toolCall.StandardError.Should().Be("actual failure");
    }

    [TestMethod]
    public void ForToolCall_ZeroExitCode_ClearsStderr()
    {
        var original = new ScriptExecutionResult
        {
            StandardOutput = "done",
            StandardError = "progress spam",
            ExitCode = 0
        };

        var toolCall = original.ForToolCall();

        toolCall.Should().NotBeSameAs(original);
        toolCall.StandardError.Should().BeEmpty();
        toolCall.StandardOutput.Should().Be("done");
    }

    [TestMethod]
    public void Truncate_LargeScriptExecutionResult_TruncatesStdoutAndAddsStderrNotice()
    {
        var largeStdout = CreateOversizedOutput('x');
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
            StandardOutput = CreateOversizedOutput('x'),
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
        var largeOutput = CreateOversizedOutput('y');

        var result = ToolOutputTruncator.Truncate(largeOutput);

        result.WasTruncated.Should().BeTrue();
        result.Output!.Length.Should().BeLessThanOrEqualTo(ToolOutputTruncator.MaxCharacters);

        using var doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("standardError").GetString().Should().Contain("Response truncated for length");
        doc.RootElement.GetProperty("standardOutput").GetString().Should().Contain("[... output truncated for length ...]");
    }

    [TestMethod]
    public void Truncate_LargeReadWebOutput_ReturnsTerminalMessageWithoutRecoveryInstructions()
    {
        var largeOutput = JsonSerializer.Serialize(new
        {
            Content = CreateOversizedOutput('z'),
            PageLinks = new Dictionary<string, string>(),
            ImageLinks = new Dictionary<string, string>()
        }, JsonOptions);

        var result = ToolOutputTruncator.Truncate(largeOutput, "GetContentFromUrl");

        result.WasTruncated.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.Output!);
        var stderr = doc.RootElement.GetProperty("standardError").GetString();
        stderr.Should().Contain("ReadWeb could not return the page");
        stderr.Should().Contain("Do not retry this ReadWeb request.");
        stderr.Should().NotContain("Write large results to a file");
        doc.RootElement.GetProperty("standardOutput").GetString().Should().BeEmpty();
    }
}
