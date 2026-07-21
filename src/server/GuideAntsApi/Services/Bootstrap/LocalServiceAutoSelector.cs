namespace GuideAntsApi.Services.Bootstrap;

public interface ILocalServiceAutoSelector
{
    Task AutoSelectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Formerly auto-activated local Emb/ASR/TTS/SD whenever the AI container was
/// reachable and no cloud provider was configured. That invented ServiceModes
/// (plus catalog default ModelIds) and forced warmup to load services the
/// operator never configured, taking the system down.
///
/// Kept as a no-op so DI/startup call sites remain stable. Local providers must
/// be activated only by explicit operator/API action with a real model selection.
/// </summary>
public sealed class LocalServiceAutoSelector : ILocalServiceAutoSelector
{
    private readonly ILogger<LocalServiceAutoSelector> _logger;

    public LocalServiceAutoSelector(ILogger<LocalServiceAutoSelector> logger)
    {
        _logger = logger;
    }

    public Task AutoSelectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "LocalServiceAutoSelector is disabled; local providers are not auto-activated at startup.");
        return Task.CompletedTask;
    }
}
