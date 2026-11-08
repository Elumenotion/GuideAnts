namespace ScriptExecutionAgent;

public sealed record ScriptExecutionRequest
{
    public string Script { get; init; } = string.Empty;
    public ScriptType ScriptType { get; init; }
    public string WorkingDirectory { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string NotebookId { get; init; } = string.Empty;
    public string? GuideId { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public class ScriptExecutionResult
{
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
}

public class ScriptExecutionConfig
{
    public int MaxScriptSize { get; set; } = 1024 * 1024;
    public TimeSpan MaxExecutionTime { get; set; } = TimeSpan.FromSeconds(600);
    public int MaxOutputSize { get; set; } = 1024 * 1024;
}

public class ValidationResult
{
    public bool IsValid { get; private set; }
    public string ErrorMessage { get; private set; } = string.Empty;

    private ValidationResult(bool isValid, string errorMessage = "")
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    public static ValidationResult Success() => new(true);
    public static ValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

public enum ScriptType
{
    Bash,
    PowerShell,
    Python
}

/// <summary>
/// Entry-point marker for in-process test hosting (WebApplicationFactory).
