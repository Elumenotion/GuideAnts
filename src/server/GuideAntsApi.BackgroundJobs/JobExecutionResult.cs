namespace GuideAntsApi.BackgroundJobs;

public sealed class JobExecutionResult
{
    public bool IsSuccess { get; init; }
    public JobFailureClass? FailureClass { get; init; }
    public string? ErrorMessage { get; init; }

    public static JobExecutionResult Success() => new() { IsSuccess = true };

    public static JobExecutionResult RetryableTransient(string? message = null) => new()
    {
        IsSuccess = false,
        FailureClass = JobFailureClass.RetryableTransient,
        ErrorMessage = message,
    };

    public static JobExecutionResult PermanentMissingInput(string? message = null) => new()
    {
        IsSuccess = false,
        FailureClass = JobFailureClass.PermanentMissingInput,
        ErrorMessage = message,
    };
}
