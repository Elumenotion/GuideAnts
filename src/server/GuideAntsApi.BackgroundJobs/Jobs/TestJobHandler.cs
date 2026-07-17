using Microsoft.Extensions.Logging;

namespace GuideAntsApi.BackgroundJobs.Jobs;

/// <summary>
/// Test job payload
/// </summary>
public record TestJob(string Message, int DelaySeconds = 1);

/// <summary>
/// Test job handler for verifying the background job system works
/// </summary>
public class TestJobHandler : JobHandlerBase<TestJob>
{
    public TestJobHandler(ILogger<TestJobHandler> logger) : base(logger)
    {
    }

    public override string JobType => "Test";

    public override async Task<JobExecutionResult> HandleAsync(TestJob payload, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Processing test job with message: {Message}", LogValueSanitizer.Sanitize(payload.Message));
        
        if (payload.DelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(payload.DelaySeconds), cancellationToken);
        }
        
        Logger.LogInformation("Test job completed successfully: {Message}", LogValueSanitizer.Sanitize(payload.Message));
        return JobExecutionResult.Success();
    }
}

