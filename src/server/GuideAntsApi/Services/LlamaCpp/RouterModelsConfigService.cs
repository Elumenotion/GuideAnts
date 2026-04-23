namespace GuideAntsApi.Services.LlamaCpp;

public sealed record RouterModelEntry(
    string Alias,
    string ModelPath,
    string MmprojPath,
    bool? HasModelFile = null,
    bool? HasMmprojFile = null,
    int? ContextSize = null,
    int? CacheRamMib = null);

public interface IRouterModelsConfigService
{
    Task<IReadOnlyList<RouterModelEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task AddOrUpdateEntryAsync(string alias, string modelContainerPath, string mmprojContainerPath, CancellationToken cancellationToken = default);
}

public sealed class RouterModelsConfigService : IRouterModelsConfigService
{
    private readonly ILlamaRuntimeAdminClient _adminClient;
    private readonly ILogger<RouterModelsConfigService> _logger;

    public RouterModelsConfigService(
        ILlamaRuntimeAdminClient adminClient,
        ILogger<RouterModelsConfigService> logger)
    {
        _adminClient = adminClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RouterModelEntry>> GetEntriesAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _adminClient.GetRouterEntriesAsync(cancellationToken).ConfigureAwait(false);
        return entries
            .Select(e => new RouterModelEntry(
                Alias: e.Alias,
                ModelPath: e.ModelPath ?? string.Empty,
                MmprojPath: e.MmprojPath ?? string.Empty,
                HasModelFile: e.HasModelFile,
                HasMmprojFile: e.HasMmprojFile,
                ContextSize: e.ContextSize,
                CacheRamMib: e.CacheRamMib))
            .ToList();
    }

    public async Task AddOrUpdateEntryAsync(
        string alias,
        string modelContainerPath,
        string mmprojContainerPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Router alias is required.", nameof(alias));
        }

        await _adminClient.AddOrUpdateRouterEntryAsync(
                alias.Trim(),
                modelContainerPath.Trim(),
                mmprojContainerPath.Trim(),
                cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Updated router alias {Alias} via llama admin service", alias);
    }

    public static IReadOnlyList<RouterModelEntry> ParseIni(string text)
    {
        var list = new List<RouterModelEntry>();
        string? currentSection = null;
        string? model = null;
        string? mmproj = null;

        void FlushSection()
        {
            if (currentSection is null || string.IsNullOrWhiteSpace(currentSection))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(mmproj))
            {
                list.Add(new RouterModelEntry(currentSection.Trim(), model ?? string.Empty, mmproj ?? string.Empty));
            }

            model = null;
            mmproj = null;
        }

        foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                FlushSection();
                currentSection = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (string.Equals(key, "version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(key, "model", StringComparison.OrdinalIgnoreCase))
            {
                model = value;
            }
            else if (string.Equals(key, "mmproj", StringComparison.OrdinalIgnoreCase))
            {
                mmproj = value;
            }
        }

        FlushSection();
        return list;
    }
}
