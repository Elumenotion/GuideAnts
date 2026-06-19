using System.Text;
using System.Text.Json;
using AntRunner.ToolCalling.Functions;

namespace AntRunner.Chat;

/// <summary>
/// Outcome of a <see cref="ToolOutputTruncator.Truncate"/> call. Carries enough metadata for the
/// caller to emit a single, structured log line when a tool result is shortened.
/// </summary>
public readonly record struct ToolOutputTruncationResult(string? Output, bool WasTruncated, int OriginalLength);

/// <summary>
/// Caps a single tool result to a hard character limit before it is added to the next LLM request,
/// so one runaway script output cannot blow past the model context window. When truncation occurs
/// the result is wrapped in a <see cref="ScriptExecutionResult"/> envelope whose <c>standardError</c>
/// explains that the response was shortened for length.
/// </summary>
public static class ToolOutputTruncator
{
    public const int MaxCharacters = 25_000;
    private const string StdoutTruncationSuffix = "\n[... output truncated for length ...]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static ToolOutputTruncationResult Truncate(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= MaxCharacters)
        {
            return new ToolOutputTruncationResult(output, false, output?.Length ?? 0);
        }

        var originalLength = output.Length;

        var truncated = TryDeserializeScriptExecutionResult(output, out var parsed)
            ? TruncateScriptExecutionResult(parsed, originalLength)
            : TruncatePlainOutput(output, originalLength);

        return new ToolOutputTruncationResult(Serialize(truncated), true, originalLength);
    }

    private static bool TryDeserializeScriptExecutionResult(string output, out ScriptExecutionResult result)
    {
        result = new ScriptExecutionResult();
        try
        {
            var parsed = JsonSerializer.Deserialize<ScriptExecutionResult>(output, JsonOptions);
            if (parsed == null)
            {
                return false;
            }

            result = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ScriptExecutionResult TruncateScriptExecutionResult(ScriptExecutionResult source, int originalLength)
    {
        var notice = BuildTruncationNotice(originalLength);
        var result = new ScriptExecutionResult
        {
            StandardOutput = source.StandardOutput ?? string.Empty,
            StandardError = AppendStderr(source.StandardError, notice),
            NewFiles = source.NewFiles,
            ModifiedFiles = source.ModifiedFiles
        };

        if (Serialize(result).Length <= MaxCharacters)
        {
            return result;
        }

        result.StandardOutput = FitStdoutToBudget(source.StandardOutput ?? string.Empty, result);
        return result;
    }

    private static ScriptExecutionResult TruncatePlainOutput(string output, int originalLength)
    {
        var notice = BuildTruncationNotice(originalLength);
        var result = new ScriptExecutionResult
        {
            StandardOutput = string.Empty,
            StandardError = notice
        };

        result.StandardOutput = FitStdoutToBudget(output, result);
        return result;
    }

    private static string FitStdoutToBudget(string stdout, ScriptExecutionResult envelope)
    {
        if (string.IsNullOrEmpty(stdout))
        {
            return string.Empty;
        }

        var low = 0;
        var high = stdout.Length;
        var best = string.Empty;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var candidateStdout = mid >= stdout.Length
                ? stdout
                : stdout[..mid] + StdoutTruncationSuffix;

            envelope.StandardOutput = candidateStdout;
            var length = Serialize(envelope).Length;
            if (length <= MaxCharacters)
            {
                best = candidateStdout;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static string AppendStderr(string? existing, string notice)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return notice;
        }

        var builder = new StringBuilder();
        builder.AppendLine(notice);
        builder.Append(existing.Trim());
        return builder.ToString();
    }

    private static string BuildTruncationNotice(int originalLength) =>
        $"Response truncated for length. Tool output exceeded the maximum of {MaxCharacters:N0} characters " +
        $"(original output was approximately {originalLength:N0} characters). " +
        "Write large results to a file and return a summary instead of full content.";

    private static string Serialize(ScriptExecutionResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);
}
