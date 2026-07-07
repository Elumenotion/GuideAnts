using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.Models.Guides;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.SandboxWireApi;

public interface ISandboxWireCycleDetector
{
    Task<bool> WouldCreateCycleAsync(
        Guid ownerAssistantId,
        Guid targetAssistantId,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> BuildAncestorChainAsync(
        Guid ownerAssistantId,
        CancellationToken ct = default);
}

public sealed class SandboxWireCycleDetector : ISandboxWireCycleDetector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApplicationDbContext _db;

    public SandboxWireCycleDetector(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<bool> WouldCreateCycleAsync(
        Guid ownerAssistantId,
        Guid targetAssistantId,
        CancellationToken ct = default)
    {
        if (ownerAssistantId == targetAssistantId)
        {
            return true;
        }

        var visited = new HashSet<Guid> { ownerAssistantId };
        var queue = new Queue<Guid>();
        queue.Enqueue(targetAssistantId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current == ownerAssistantId)
            {
                return true;
            }

            var configJson = await _db.Assistants
                .AsNoTracking()
                .Where(a => a.Id == current && a.Kind == DataModel.Models.AssistantKind.Guide)
                .Select(a => a.SandboxWireApiConfigJson)
                .FirstOrDefaultAsync(ct);

            var config = DeserializeConfig(configJson);
            if (!config.Enabled || !config.TargetAssistantId.HasValue)
            {
                continue;
            }

            queue.Enqueue(config.TargetAssistantId.Value);
        }

        return false;
    }

    public async Task<IReadOnlyList<Guid>> BuildAncestorChainAsync(
        Guid ownerAssistantId,
        CancellationToken ct = default)
    {
        var ancestors = new List<Guid> { ownerAssistantId };
        var visited = new HashSet<Guid> { ownerAssistantId };
        var current = ownerAssistantId;

        while (true)
        {
            var configJson = await _db.Assistants
                .AsNoTracking()
                .Where(a => a.Id == current && a.Kind == DataModel.Models.AssistantKind.Guide)
                .Select(a => a.SandboxWireApiConfigJson)
                .FirstOrDefaultAsync(ct);

            var config = DeserializeConfig(configJson);
            if (!config.Enabled || !config.TargetAssistantId.HasValue)
            {
                break;
            }

            var next = config.TargetAssistantId.Value;
            if (!visited.Add(next))
            {
                break;
            }

            ancestors.Add(next);
            current = next;
        }

        return ancestors;
    }

    private static SandboxWireApiConfigDto DeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SandboxWireApiConfigDto();
        }

        return JsonSerializer.Deserialize<SandboxWireApiConfigDto>(json, JsonOptions)
            ?? new SandboxWireApiConfigDto();
    }
}
