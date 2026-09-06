using System.Collections.Concurrent;

namespace GuideAntsApi.Services.LlamaCpp;

public interface INotebookChatAliasState
{
    /// <summary>Router alias currently kept loaded on behalf of a notebook/assistant chat session, or null.</summary>
    string? ActiveChatAlias { get; }

    void SetActiveChatAlias(string routerAlias);

    void ClearActiveChatAlias(string? routerAlias = null);
}

/// <summary>
/// Singleton, process-local record of the llama router alias that a notebook load
/// operation put up for assistant chat. The desired-state plan builder consults
/// this so lifecycle applies (ASR/TTS recycle, routed warmup restore) do not emit
/// llama.enabled=false and tear down an in-use local chat model when the guide's
/// configured chat model is cloud-hosted and ChatDefaults has no local default.
/// </summary>
public sealed class NotebookChatAliasState : INotebookChatAliasState
{
    private readonly ConcurrentDictionary<string, byte> _aliases = new(StringComparer.Ordinal);
    private volatile string? _primaryAlias;

    public string? ActiveChatAlias => _primaryAlias ?? _aliases.Keys.FirstOrDefault();

    public void SetActiveChatAlias(string routerAlias)
    {
        if (string.IsNullOrWhiteSpace(routerAlias)) return;
        var normalized = routerAlias.Trim();
        _aliases.TryAdd(normalized, 0);
        _primaryAlias = normalized;
    }

    public void ClearActiveChatAlias(string? routerAlias = null)
    {
        if (string.IsNullOrWhiteSpace(routerAlias))
        {
            _aliases.Clear();
            _primaryAlias = null;
            return;
        }

        var normalized = routerAlias.Trim();
        _aliases.TryRemove(normalized, out _);
        if (string.Equals(_primaryAlias, normalized, StringComparison.Ordinal))
        {
            _primaryAlias = _aliases.Keys.FirstOrDefault();
        }
    }
}
