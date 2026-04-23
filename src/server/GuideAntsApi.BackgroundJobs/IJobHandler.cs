namespace GuideAntsApi.BackgroundJobs;

/// <summary>
/// Base interface for job handlers
/// </summary>
public interface IJobHandler
{
    string JobType { get; }
    Task<bool> HandleAsync(string payloadJson, CancellationToken cancellationToken);
}

/// <summary>
/// Strongly-typed job handler interface
/// </summary>
/// <typeparam name="TPayload">The payload type for this job</typeparam>
public interface IJobHandler<TPayload> : IJobHandler
{
    Task<bool> HandleAsync(TPayload payload, CancellationToken cancellationToken);
}

