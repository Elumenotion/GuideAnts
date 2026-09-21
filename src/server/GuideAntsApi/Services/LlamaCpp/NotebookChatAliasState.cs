using System.Collections.Concurrent;
using GuideAntsApi.Services.Bootstrap;

namespace GuideAntsApi.Services.LlamaCpp;

public interface INotebookChatAliasState
{
    /// <summary>
    /// Legacy single-alias view for existing callers: the most recent notebook chat
    /// alias, or null. Per-instance callers should use <see cref="GetActiveChatAlias"/>
    /// and <see cref="GetActiveChatAliases"/>.
    /// </summary>
    string? ActiveChatAlias { get; }

    /// <summary>
    /// The notebook chat alias kept loaded on the given instance (canonical identity), or null.
    /// </summary>
    string? GetActiveChatAlias(string? instanceBase);

    /// <summary>
    /// All instances (canonical identity) that currently keep a notebook chat alias
    /// loaded, with their aliases. Instances are independent: each entry is owned by
    /// its instance.
    /// </summary>
    IReadOnlyDictionary<string, string> GetActiveChatAliases();

    void SetActiveChatAlias(string routerAlias);

    /// <summary>Records the notebook chat alias loaded on a specific instance.</summary>
    void SetActiveChatAliasForInstance(string? instanceBase, string routerAlias);

    void ClearActiveChatAlias(string? routerAlias = null);

    /// <summary>Forgets the alias state for one instance (does not actuate anything).</summary>
    void ClearInstance(string? instanceBase);
}

/// <summary>
/// Singleton, process-local record of the llama router aliases that notebook load
/// operations put up for assistant chat, keyed by instance base. The desired-state
/// plan builder consults this so lifecycle applies (ASR/TTS recycle, routed warmup
/// restore) emit a per-instance llama section that keeps each instance's notebook
/// alias loaded instead of tearing it down via a ChatDefaults-only plan. Instances
/// are independent: state for one instance never affects another.
/// </summary>
public sealed class NotebookChatAliasState : INotebookChatAliasState
{
    private readonly ConcurrentDictionary<string, string> _aliasesByInstance = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _primaryAlias;

    public string? ActiveChatAlias => _primaryAlias ?? GetActiveChatAliases().Values.FirstOrDefault();

    public string? GetActiveChatAlias(string? instanceBase)
    {
        var key = NormalizeInstanceKey(instanceBase);
        return key is null
            ? ActiveChatAlias
            : _aliasesByInstance.TryGetValue(key, out var alias) ? alias : null;
    }

    public IReadOnlyDictionary<string, string> GetActiveChatAliases()
    {
        return _aliasesByInstance.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(_aliasesByInstance, StringComparer.OrdinalIgnoreCase);
    }

    public void SetActiveChatAlias(string routerAlias)
    {
        if (string.IsNullOrWhiteSpace(routerAlias)) return;
        var normalized = routerAlias.Trim();
        _primaryAlias = normalized;
        if (_aliasesByInstance.Count == 0)
        {
            _aliasesByInstance[UnknownInstanceKey] = normalized;
        }
    }

    public void SetActiveChatAliasForInstance(string? instanceBase, string routerAlias)
    {
        if (string.IsNullOrWhiteSpace(routerAlias)) return;
        var normalized = routerAlias.Trim();
        _primaryAlias = normalized;
        _aliasesByInstance[NormalizeInstanceKey(instanceBase) ?? UnknownInstanceKey] = normalized;
    }

    public void ClearActiveChatAlias(string? routerAlias = null)
    {
        if (string.IsNullOrWhiteSpace(routerAlias))
        {
            _aliasesByInstance.Clear();
            _primaryAlias = null;
            return;
        }

        var normalized = routerAlias.Trim();
        foreach (var key in _aliasesByInstance.Keys.ToList())
        {
            if (string.Equals(_aliasesByInstance[key], normalized, StringComparison.Ordinal))
            {
                _aliasesByInstance.TryRemove(key, out _);
            }
        }
        if (string.Equals(_primaryAlias, normalized, StringComparison.Ordinal))
        {
            _primaryAlias = GetActiveChatAliases().Values.FirstOrDefault();
        }
    }

    public void ClearInstance(string? instanceBase)
    {
        var key = NormalizeInstanceKey(instanceBase);
        if (key is null)
        {
            return;
        }

        if (_aliasesByInstance.TryRemove(key, out _))
        {
            _primaryAlias = GetActiveChatAliases().Values.FirstOrDefault();
        }
    }

    /// <summary>
    /// Keys alias state by canonical instance identity (host resolved to IP:port), the
    /// same identity the warmup plan builder and runtime service use for per-instance
    /// sections. Callers pass either a canonical key or a raw stack base URL; both
    /// normalize to the same entry.
    /// </summary>
    private static string? NormalizeInstanceKey(string? instanceBase)
    {
        if (string.IsNullOrWhiteSpace(instanceBase))
        {
            return null;
        }

        var trimmed = instanceBase.Trim().TrimEnd('/');
        try
        {
            return LocalAiStackHostResolver.CanonicalInstanceKey(trimmed);
        }
        catch (UriFormatException)
        {
            return trimmed;
        }
    }

    private const string UnknownInstanceKey = "unknown";
}
