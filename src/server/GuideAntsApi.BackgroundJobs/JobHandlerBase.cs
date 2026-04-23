using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GuideAntsApi.BackgroundJobs;

/// <summary>
/// Base class for strongly-typed job handlers
/// </summary>
/// <typeparam name="TPayload">The payload type for this job</typeparam>
public abstract class JobHandlerBase<TPayload> : IJobHandler<TPayload>
{
    protected readonly ILogger Logger;

    protected JobHandlerBase(ILogger logger)
    {
        Logger = logger;
    }

    public abstract string JobType { get; }

    public abstract Task<bool> HandleAsync(TPayload payload, CancellationToken cancellationToken);

    public async Task<bool> HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<TPayload>(payloadJson);
            if (payload == null)
            {
                Logger.LogError("Failed to deserialize payload for job type {JobType}: {PayloadJson}", JobType, payloadJson);
                return false;
            }

            return await HandleAsync(payload, cancellationToken);
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "JSON deserialization failed for job type {JobType}: {PayloadJson}", JobType, payloadJson);
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error handling job type {JobType}", JobType);
            return false;
        }
    }
}

