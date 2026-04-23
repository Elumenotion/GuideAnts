using GuideAntsApi.BackgroundJobs.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GuideAntsApi.BackgroundJobs.Services;

/// <summary>
/// Global, process-level limiter for docling conversion requests.
/// Keeps pressure on docling-serve bounded even when multiple background
/// job types trigger extraction concurrently.
/// </summary>
internal sealed class DoclingConversionLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger<DoclingConversionLimiter> _logger;

    public int MaxConcurrentConversions { get; }

    public DoclingConversionLimiter(
        IOptionsMonitor<DocumentIntelligenceOptions> optionsMonitor,
        ILogger<DoclingConversionLimiter> logger)
    {
        _logger = logger;

        MaxConcurrentConversions = Math.Max(1, optionsMonitor.CurrentValue.MaxConcurrentConversions);
        _semaphore = new SemaphoreSlim(MaxConcurrentConversions, MaxConcurrentConversions);

        if (optionsMonitor.CurrentValue.MaxConcurrentConversions < 1)
        {
            _logger.LogWarning(
                "DocumentIntelligence:MaxConcurrentConversions was {ConfiguredValue}; using {EffectiveValue}.",
                optionsMonitor.CurrentValue.MaxConcurrentConversions,
                MaxConcurrentConversions);
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();
}
