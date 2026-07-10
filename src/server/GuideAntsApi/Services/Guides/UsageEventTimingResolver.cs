using System.Text.Json;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using Microsoft.EntityFrameworkCore;

namespace GuideAntsApi.Services.Guides;

internal readonly record struct UsageEventTiming(DateTime Start, DateTime End, long DurationMs);

/// <summary>
/// Deterministic tool timing from message history: every tool execution has an assistant
/// request row (toolCalls) and a tool result row (toolCallId). Duration is always
/// result.Created - request.Created for the matching pair.
/// </summary>
internal sealed class UsageEventTimingResolver
{
    private static readonly Dictionary<string, string[]> ServiceOperationToToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["image-generation"] = ["generate_image", "MakeImageFromImage"],
        };

    private readonly Dictionary<string, ToolCallTiming> _pairByScopeAndToolCallId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ToolCallTiming> _pairByToolResultMessageId = new();
    private readonly Dictionary<string, List<ToolCallTiming>> _pairsByScopeAndFunction = new(StringComparer.OrdinalIgnoreCase);

    private sealed record ToolCallTiming(
        string ToolCallId,
        string? FunctionName,
        Guid ScopeId,
        DateTime StartUtc,
        DateTime EndUtc,
        Guid? ToolResultMessageId)
    {
        public UsageEventTiming ToUsageEventTiming() =>
            new(StartUtc, EndUtc, Math.Max(0L, (long)(EndUtc - StartUtc).TotalMilliseconds));
    }

    public static async Task<UsageEventTimingResolver> CreateAsync(
        ApplicationDbContext context,
        Guid conversationId,
        IEnumerable<Guid> invocationIds,
        CancellationToken ct = default)
    {
        var resolver = new UsageEventTimingResolver();
        var invocationIdList = invocationIds.Distinct().ToList();
        var pendingStarts = new Dictionary<string, (string? FunctionName, DateTime StartUtc)>(StringComparer.OrdinalIgnoreCase);

        if (invocationIdList.Count > 0)
        {
            var invocationMessages = await context.AgentInvocationMessages
                .AsNoTracking()
                .Where(m => invocationIdList.Contains(m.AgentInvocationId))
                .OrderBy(m => m.Sequence)
                .Select(m => new
                {
                    m.AgentInvocationId,
                    m.Role,
                    m.Created,
                    m.ToolCallId,
                    m.ToolCallsJson,
                    m.FunctionName,
                })
                .ToListAsync(ct);

            foreach (var message in invocationMessages)
            {
                if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.ToolCallsJson))
                {
                    foreach (var toolCall in ParseToolCalls(message.ToolCallsJson))
                    {
                        pendingStarts[PairKey(message.AgentInvocationId, toolCall.Id)] = (toolCall.Name, message.Created);
                    }
                }
            }

            foreach (var message in invocationMessages)
            {
                if (message.Role != ChatRole.Tool || string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    continue;
                }

                resolver.RegisterPair(
                    scopeId: message.AgentInvocationId,
                    toolCallId: message.ToolCallId!,
                    functionName: message.FunctionName,
                    pendingStarts,
                    toolResultMessageId: null,
                    endUtc: message.Created);
            }
        }

        var notebookMessages = await context.NotebookConversationMessages
            .AsNoTracking()
            .Where(m => m.NotebookConversationId == conversationId)
            .OrderBy(m => m.TurnIndex)
            .ThenBy(m => m.MessageSequence)
            .Select(m => new
            {
                m.Id,
                m.Role,
                m.Created,
                m.ToolCallId,
                m.ToolCalls,
                m.FunctionName,
            })
            .ToListAsync(ct);

        foreach (var message in notebookMessages)
        {
            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.ToolCalls))
            {
                foreach (var toolCall in ParseToolCalls(message.ToolCalls))
                {
                    pendingStarts[PairKey(Guid.Empty, toolCall.Id)] = (toolCall.Name, message.Created);
                }
            }
        }

        foreach (var message in notebookMessages)
        {
            if (message.Role != ChatRole.Tool || string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                continue;
            }

            resolver.RegisterPair(
                scopeId: Guid.Empty,
                toolCallId: message.ToolCallId!,
                functionName: message.FunctionName,
                pendingStarts,
                toolResultMessageId: message.Id,
                endUtc: message.Created);
        }

        return resolver;
    }

    public UsageEventTiming Resolve(UsageEvent usageEvent)
    {
        var scopeId = usageEvent.AgentInvocationId ?? Guid.Empty;

        if (TryResolvePair(usageEvent, scopeId, out var timing))
        {
            return timing.ToUsageEventTiming();
        }

        return new UsageEventTiming(usageEvent.Created, usageEvent.Created, 0);
    }

    private bool TryResolvePair(UsageEvent usageEvent, Guid scopeId, out ToolCallTiming timing)
    {
        if (usageEvent.NotebookConversationMessageId is Guid toolMessageId
            && _pairByToolResultMessageId.TryGetValue(toolMessageId, out timing!))
        {
            return true;
        }

        var toolCallId = TryGetToolCallId(usageEvent.MetadataJson);
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            if (TryGetPair(scopeId, toolCallId, out timing))
            {
                return true;
            }

            if (scopeId != Guid.Empty && TryGetPair(Guid.Empty, toolCallId, out timing))
            {
                return true;
            }
        }

        foreach (var functionName in GetCandidateFunctionNames(usageEvent))
        {
            if (TryGetPairByFunction(scopeId, functionName, usageEvent.Created, out timing))
            {
                return true;
            }

            if (scopeId != Guid.Empty
                && TryGetPairByFunction(Guid.Empty, functionName, usageEvent.Created, out timing))
            {
                return true;
            }
        }

        timing = null!;
        return false;
    }

    private void RegisterPair(
        Guid scopeId,
        string toolCallId,
        string? functionName,
        Dictionary<string, (string? FunctionName, DateTime StartUtc)> pendingStarts,
        Guid? toolResultMessageId,
        DateTime endUtc)
    {
        if (!pendingStarts.TryGetValue(PairKey(scopeId, toolCallId), out var pending))
        {
            return;
        }

        var pair = new ToolCallTiming(
            toolCallId,
            functionName ?? pending.FunctionName,
            scopeId,
            pending.StartUtc,
            endUtc,
            toolResultMessageId);

        _pairByScopeAndToolCallId[PairKey(scopeId, toolCallId)] = pair;

        if (toolResultMessageId is Guid messageId)
        {
            _pairByToolResultMessageId[messageId] = pair;
        }

        if (!string.IsNullOrWhiteSpace(pair.FunctionName))
        {
            var functionKey = FunctionKey(scopeId, pair.FunctionName);
            if (!_pairsByScopeAndFunction.TryGetValue(functionKey, out var list))
            {
                list = [];
                _pairsByScopeAndFunction[functionKey] = list;
            }

            list.Add(pair);
            list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        }
    }

    private bool TryGetPair(Guid scopeId, string toolCallId, out ToolCallTiming timing) =>
        _pairByScopeAndToolCallId.TryGetValue(PairKey(scopeId, toolCallId), out timing!);

    private bool TryGetPairByFunction(
        Guid scopeId,
        string functionName,
        DateTime usageCreated,
        out ToolCallTiming timing)
    {
        timing = null!;
        if (!_pairsByScopeAndFunction.TryGetValue(FunctionKey(scopeId, functionName), out var candidates)
            || candidates.Count == 0)
        {
            return false;
        }

        if (candidates.Count == 1)
        {
            timing = candidates[0];
            return true;
        }

        // Multiple calls to the same tool: pick the pair whose result timestamp is closest
        // to (and not after) the usage event. This is deterministic given ordered history.
        ToolCallTiming? best = null;
        var bestDelta = long.MaxValue;
        foreach (var candidate in candidates)
        {
            var delta = Math.Abs((candidate.EndUtc - usageCreated).Ticks);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = candidate;
            }
        }

        if (best == null)
        {
            return false;
        }

        timing = best;
        return true;
    }

    private static IEnumerable<string> GetCandidateFunctionNames(UsageEvent usageEvent)
    {
        if (usageEvent.Category == UsageCategory.ToolCall
            && !string.IsNullOrWhiteSpace(usageEvent.Operation))
        {
            yield return usageEvent.Operation;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(usageEvent.Operation)
            && ServiceOperationToToolNames.TryGetValue(usageEvent.Operation, out var mapped))
        {
            foreach (var name in mapped)
            {
                yield return name;
            }

            yield break;
        }

        if (usageEvent.Category == UsageCategory.SpeechSynthesis)
        {
            yield return "generate_podcast";
        }
    }

    private static string PairKey(Guid scopeId, string toolCallId) => $"{scopeId:N}:{toolCallId}";

    private static string FunctionKey(Guid scopeId, string functionName) => $"{scopeId:N}:{functionName}";

    private static string? TryGetToolCallId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.TryGetProperty("toolCallId", out var property))
            {
                return property.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<(string Id, string? Name)> ParseToolCalls(string toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(toolCallsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<(string Id, string? Name)>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idProperty))
                {
                    continue;
                }

                var id = idProperty.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string? name = null;
                if (element.TryGetProperty("function", out var functionProperty)
                    && functionProperty.TryGetProperty("name", out var nameProperty))
                {
                    name = nameProperty.GetString();
                }

                results.Add((id, name));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
