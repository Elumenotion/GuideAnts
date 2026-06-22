using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides;

/// <summary>
/// Service for querying guide/assistant usage statistics and invocation details.
/// </summary>
public class GuideUsageService : IGuideUsageService
{
    private readonly ApplicationDbContext _context;
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly ILogger<GuideUsageService>? _logger;

    // Helper aggregate type for per-conversation usage joins
    private sealed record ConversationAggregate(
        Guid ConversationId,
        int ToolCalls,
        long PromptTokens,
        long CachedTokens,
        long ReasoningTokens,
        long CompletionTokens,
        decimal Cost);

    private sealed record InvocationTokenAggregate(long PromptTokens, long CompletionTokens);

    // Consolidated direct usage row for efficient single-query aggregation
    private sealed record DirectUsageRow(
        DateTime Date,
        UsageCategory Category,
        string? Operation,
        Guid? ConversationId,
        int Count,
        long PromptTokens,
        long CachedTokens,
        long ReasoningTokens,
        long CompletionTokens,
        decimal Cost);

    // Consolidated crew usage row for efficient single-query aggregation
    private sealed record CrewUsageRow(
        DateTime Date,
        UsageCategory Category,
        Guid? ConversationId,
        int Count,
        long PromptTokens,
        long CachedTokens,
        long ReasoningTokens,
        long CompletionTokens,
        decimal Cost);

    private sealed record ApiUsageSlice(
        string? SourceChannel,
        string? Operation,
        string? MetadataJson,
        decimal ChargeUsd);

    // Assistants that should never appear in guide usage drill-downs.
    private static readonly HashSet<string> ExcludedAssistantNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Conversation Title Generator"
        };

    public GuideUsageService(
        ApplicationDbContext context,
        IDbContextFactory<ApplicationDbContext> contextFactory,
        ILogger<GuideUsageService>? logger = null)
    {
        _context = context;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    private async Task<(Guid Id, string Name)?> GetOwnedAssistantAsync(Guid assistantId)
    {
        var guide = await _context.Assistants
            .Where(a => a.Id == assistantId)
            .Select(a => new { a.Id, a.Name })
            .FirstOrDefaultAsync();

        return guide == null ? null : (guide.Id, guide.Name);
    }

    public async Task<GuideUsageSummaryDto?> GetGuideUsageSummaryAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to)
    {
        var guide = await GetOwnedAssistantAsync(guideId);
        if (guide == null) return null;

        var directSummaryTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            var rows = await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId == null
                         && e.Created >= from
                         && e.Created <= to)
                .GroupBy(e => e.Category)
                .Select(g => new
                {
                    Category = g.Key,
                    Prompt = g.Sum(e => e.ValueInput),
                    Cached = g.Sum(e => e.ValueCachedInput),
                    Reasoning = g.Sum(e => e.ValueReasoning),
                    Completion = g.Sum(e => e.ValueOutput),
                    Cost = g.Sum(e => e.ChargeUsd ?? 0m)
                })
                .ToListAsync();
            return rows;
        });

        var guideConversationIdsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId == null
                         && e.ConversationId != null
                         && e.Created >= from
                         && e.Created <= to)
                .Select(e => e.ConversationId!.Value)
                .Distinct()
                .ToListAsync();
        });

        var totalConversationsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.ConversationId != null
                         && e.Created >= from
                         && e.Created <= to)
                .Select(e => e.ConversationId)
                .Distinct()
                .CountAsync();
        });

        await Task.WhenAll(directSummaryTask, guideConversationIdsTask, totalConversationsTask);

        var directSummary = await directSummaryTask;
        var guideConversationIds = await guideConversationIdsTask;
        var totalConversations = await totalConversationsTask;

        await using var crewSummaryCtx = await _contextFactory.CreateDbContextAsync();
        var crewSummary = await crewSummaryCtx.UsageEvents
            .Where(e => e.ProjectId == projectId
                     && e.AgentInvocationId != null
                     && e.Created >= from
                     && e.Created <= to
                     && (
                         e.InvokingAssistantId == guideId ||
                         (e.InvokingAssistantId == null
                          && e.ConversationId != null
                          && guideConversationIds.Contains(e.ConversationId.Value))
                     ))
            .GroupBy(e => e.Category)
            .Select(g => new
            {
                Category = g.Key,
                Prompt = g.Sum(e => e.ValueInput),
                Cached = g.Sum(e => e.ValueCachedInput),
                Reasoning = g.Sum(e => e.ValueReasoning),
                Completion = g.Sum(e => e.ValueOutput),
                Cost = g.Sum(e => e.ChargeUsd ?? 0m)
            })
            .ToListAsync();

        var directChat = directSummary.FirstOrDefault(x => x.Category == UsageCategory.ChatCompletion);
        var crewChat = crewSummary.FirstOrDefault(x => x.Category == UsageCategory.ChatCompletion);

        var directCost = directSummary.Sum(x => x.Cost);
        var crewCost = crewSummary.Sum(x => x.Cost);

        return new GuideUsageSummaryDto(
            guide.Value.Id,
            guide.Value.Name,
            from,
            to,
            totalConversations,
            directChat?.Prompt ?? 0,
            directChat?.Cached ?? 0,
            directChat?.Reasoning ?? 0,
            directChat?.Completion ?? 0,
            (directChat?.Prompt ?? 0) + (crewChat?.Prompt ?? 0),
            (directChat?.Cached ?? 0) + (crewChat?.Cached ?? 0),
            (directChat?.Reasoning ?? 0) + (crewChat?.Reasoning ?? 0),
            (directChat?.Completion ?? 0) + (crewChat?.Completion ?? 0),
            directCost,
            directCost + crewCost);
    }

    public async Task<List<DailyUsageBucketDto>?> GetGuideUsageDailyBucketsAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to)
    {
        var guide = await GetOwnedAssistantAsync(guideId);
        if (guide == null) return null;

        await using var ctx = await _contextFactory.CreateDbContextAsync();

        var guideConversationIds = await ctx.UsageEvents
            .Where(e => e.AssistantId == guideId
                     && e.ProjectId == projectId
                     && e.AgentInvocationId == null
                     && e.ConversationId != null
                     && e.Created >= from
                     && e.Created <= to)
            .Select(e => e.ConversationId!.Value)
            .Distinct()
            .ToListAsync();

        var directDaily = await ctx.UsageEvents
            .Where(e => e.AssistantId == guideId
                     && e.ProjectId == projectId
                     && e.AgentInvocationId == null
                     && e.Created >= from
                     && e.Created <= to)
            .GroupBy(e => new { e.Created.Date, e.Category })
            .Select(g => new
            {
                g.Key.Date,
                g.Key.Category,
                Prompt = g.Sum(e => e.ValueInput),
                Cached = g.Sum(e => e.ValueCachedInput),
                Reasoning = g.Sum(e => e.ValueReasoning),
                Completion = g.Sum(e => e.ValueOutput),
                Cost = g.Sum(e => e.ChargeUsd ?? 0m)
            })
            .ToListAsync();

        var crewDaily = await ctx.UsageEvents
            .Where(e => e.ProjectId == projectId
                     && e.AgentInvocationId != null
                     && e.Created >= from
                     && e.Created <= to
                     && (
                         e.InvokingAssistantId == guideId ||
                         (e.InvokingAssistantId == null
                          && e.ConversationId != null
                          && guideConversationIds.Contains(e.ConversationId.Value))
                     ))
            .GroupBy(e => new { e.Created.Date, e.Category })
            .Select(g => new
            {
                g.Key.Date,
                g.Key.Category,
                Prompt = g.Sum(e => e.ValueInput),
                Cached = g.Sum(e => e.ValueCachedInput),
                Reasoning = g.Sum(e => e.ValueReasoning),
                Completion = g.Sum(e => e.ValueOutput),
                Cost = g.Sum(e => e.ChargeUsd ?? 0m)
            })
            .ToListAsync();

        var directByDate = directDaily
            .GroupBy(x => x.Date)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Prompt = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Prompt),
                    Cached = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Cached),
                    Reasoning = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Reasoning),
                    Completion = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Completion),
                    Cost = g.Sum(x => x.Cost)
                });

        var crewByDate = crewDaily
            .GroupBy(x => x.Date)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Prompt = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Prompt),
                    Cached = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Cached),
                    Reasoning = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Reasoning),
                    Completion = g.Where(x => x.Category == UsageCategory.ChatCompletion).Sum(x => x.Completion),
                    Cost = g.Sum(x => x.Cost)
                });

        var allDates = directByDate.Keys.Union(crewByDate.Keys).OrderBy(d => d).ToList();

        return allDates.Select(date =>
        {
            var d = directByDate.GetValueOrDefault(date);
            var c = crewByDate.GetValueOrDefault(date);
            var directPrompt = d?.Prompt ?? 0;
            var directCached = d?.Cached ?? 0;
            var directReasoning = d?.Reasoning ?? 0;
            var directCompletion = d?.Completion ?? 0;
            var directCost = d?.Cost ?? 0m;

            return new DailyUsageBucketDto(
                date,
                directPrompt,
                directCached,
                directReasoning,
                directCompletion,
                directPrompt + (c?.Prompt ?? 0),
                directCached + (c?.Cached ?? 0),
                directReasoning + (c?.Reasoning ?? 0),
                directCompletion + (c?.Completion ?? 0),
                directCost,
                directCost + (c?.Cost ?? 0m));
        }).ToList();
    }

    public async Task<GuideUsageCrewDto?> GetGuideUsageCrewAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to)
    {
        var guide = await GetOwnedAssistantAsync(guideId);
        if (guide == null) return null;

        var crewMembersTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.Set<GuideMember>()
                .Where(gm => gm.GuideId == guideId)
                .Join(ctx.Assistants, gm => gm.AssistantId, a => a.Id, (gm, a) => new { gm.AssistantId, a.Name, gm.DisplayOrder })
                .OrderBy(x => x.DisplayOrder == null ? 1 : 0)
                .ThenBy(x => x.DisplayOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();
        });

        var totalConversationsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.ConversationId != null
                         && e.Created >= from
                         && e.Created <= to)
                .Select(e => e.ConversationId)
                .Distinct()
                .CountAsync();
        });

        var guideConversationIdsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId == null
                         && e.ConversationId != null
                         && e.Created >= from
                         && e.Created <= to)
                .Select(e => e.ConversationId!.Value)
                .Distinct()
                .ToListAsync();
        });

        var directToolCallsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId == null
                         && e.Category == UsageCategory.ToolCall
                         && e.Created >= from
                         && e.Created <= to
                         && (e.MetadataJson == null || !e.MetadataJson.Contains("\"assistantName\"")))
                .GroupBy(e => e.Operation)
                .Select(g => new { ToolName = g.Key, Count = g.Count(), TotalCost = g.Sum(e => e.ChargeUsd ?? 0m) })
                .ToListAsync();
        });

        await Task.WhenAll(crewMembersTask, totalConversationsTask, guideConversationIdsTask, directToolCallsTask);
        var crewMembers = await crewMembersTask;
        var totalConversations = await totalConversationsTask;
        var guideConversationIds = await guideConversationIdsTask;
        var directToolCalls = await directToolCallsTask;

        var crewAssistantIds = crewMembers.Select(cm => cm.AssistantId).ToList();
        await using var crewTotalsCtx = await _contextFactory.CreateDbContextAsync();
        var crewMemberTotals = await crewTotalsCtx.UsageEvents
            .Where(e => e.ProjectId == projectId
                     && e.AgentInvocationId != null
                     && e.AssistantId != null
                     && crewAssistantIds.Contains(e.AssistantId.Value)
                     && e.Created >= from
                     && e.Created <= to
                     && (
                         e.InvokingAssistantId == guideId ||
                         (e.InvokingAssistantId == null
                          && e.ConversationId != null
                          && guideConversationIds.Contains(e.ConversationId.Value))
                     ))
            .GroupBy(e => e.AssistantId)
            .Select(g => new
            {
                AssistantId = g.Key!.Value,
                InvocationCount = g.Select(e => e.AgentInvocationId).Distinct().Count(),
                TotalCost = g.Sum(e => e.ChargeUsd ?? 0m)
            })
            .ToListAsync();

        var crewTotalsByAssistant = crewMemberTotals.ToDictionary(x => x.AssistantId, x => x);
        var crewDtos = crewMembers.Select(cm =>
        {
            crewTotalsByAssistant.TryGetValue(cm.AssistantId, out var totals);
            var invocations = totals?.InvocationCount ?? 0;
            var cost = totals?.TotalCost ?? 0m;
            var avgInvocations = totalConversations > 0 ? (double)invocations / totalConversations : 0.0;
            var avgCost = totalConversations > 0 ? cost / totalConversations : 0m;
            return new CrewMemberUsageDto(cm.AssistantId, cm.Name, invocations, avgInvocations, cost, avgCost);
        }).ToList();

        var toolDtos = directToolCalls.OrderBy(t => t.ToolName).Select(tool =>
        {
            var avgCalls = totalConversations > 0 ? (double)tool.Count / totalConversations : 0.0;
            var avgCost = totalConversations > 0 ? tool.TotalCost / totalConversations : 0m;
            return new ToolUsageDto(tool.ToolName ?? "unknown", tool.Count, avgCalls, tool.TotalCost, avgCost);
        }).ToList();

        return new GuideUsageCrewDto(crewDtos, toolDtos);
    }

    public async Task<GuideUsageConversationsPageDto?> GetGuideUsageConversationsAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to,
        int page,
        int pageSize)
    {
        var guide = await GetOwnedAssistantAsync(guideId);
        if (guide == null) return null;

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var directConversationUsage = await _context.UsageEvents
            .Where(e => e.AssistantId == guideId
                     && e.ProjectId == projectId
                     && e.AgentInvocationId == null
                     && e.ConversationId != null
                     && e.Created >= from
                     && e.Created <= to)
            .GroupBy(e => e.ConversationId!.Value)
            .Select(g => new ConversationAggregate(
                g.Key,
                g.Count(e => e.Category == UsageCategory.ToolCall),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueInput),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueCachedInput),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueReasoning),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueOutput),
                g.Sum(e => e.ChargeUsd ?? 0m)))
            .ToListAsync();

        var guideConversationIds = directConversationUsage.Select(c => c.ConversationId).ToList();

        var crewConversationUsage = await _context.UsageEvents
            .Where(e => e.ProjectId == projectId
                     && e.AgentInvocationId != null
                     && e.ConversationId != null
                     && e.Created >= from
                     && e.Created <= to
                     && (
                         e.InvokingAssistantId == guideId ||
                         (e.InvokingAssistantId == null
                          && guideConversationIds.Contains(e.ConversationId.Value))
                     ))
            .GroupBy(e => e.ConversationId!.Value)
            .Select(g => new ConversationAggregate(
                g.Key,
                g.Count(e => e.Category == UsageCategory.ToolCall),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueInput),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueCachedInput),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueReasoning),
                g.Where(e => e.Category == UsageCategory.ChatCompletion).Sum(e => e.ValueOutput),
                g.Sum(e => e.ChargeUsd ?? 0m)))
            .ToListAsync();

        var conversationIdSet = directConversationUsage
            .Select(c => c.ConversationId)
            .Union(crewConversationUsage.Select(c => c.ConversationId))
            .Distinct()
            .ToList();

        var totalCount = conversationIdSet.Count;
        if (totalCount == 0)
        {
            return new GuideUsageConversationsPageDto(page, pageSize, 0, []);
        }

        var crewMembers = await _context.Set<GuideMember>()
            .Where(gm => gm.GuideId == guideId)
            .Select(gm => gm.AssistantId)
            .ToListAsync();
        var allowedAssistantIds = new HashSet<Guid>(crewMembers) { guideId };

        var conversationMetadata = await _context.NotebookConversations
            .Where(c => conversationIdSet.Contains(c.Id))
            .OrderByDescending(c => c.Created)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new { c.Id, c.NotebookId, c.Title, c.Created })
            .ToListAsync();

        var pageIds = conversationMetadata.Select(c => c.Id).ToList();

        var assistantSteps = await _context.NotebookConversationMessages
            .Where(m => pageIds.Contains(m.NotebookConversationId)
                        && m.Role == ChatRole.Assistant
                        && m.AssistantId != null
                        && allowedAssistantIds.Contains(m.AssistantId.Value)
                        && (m.AssistantName == null || !ExcludedAssistantNames.Contains(m.AssistantName)))
            .GroupBy(m => m.NotebookConversationId)
            .Select(g => new { ConversationId = g.Key, Steps = g.Count() })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Steps);

        var turnIndicesByConversation = await _context.ConversationTurns
            .Where(t => pageIds.Contains(t.NotebookConversationId))
            .GroupBy(t => t.NotebookConversationId)
            .Select(g => new { ConversationId = g.Key, TurnIndices = g.Select(t => t.TurnIndex).Distinct().OrderBy(x => x).ToList() })
            .ToDictionaryAsync(x => x.ConversationId, x => (IReadOnlyList<int>)x.TurnIndices);

        var directByConversation = directConversationUsage.ToDictionary(c => c.ConversationId);
        var crewByConversation = crewConversationUsage.ToDictionary(c => c.ConversationId);

        var items = conversationMetadata.Select(c =>
        {
            directByConversation.TryGetValue(c.Id, out var direct);
            crewByConversation.TryGetValue(c.Id, out var crew);

            var totalToolCalls = (direct?.ToolCalls ?? 0) + (crew?.ToolCalls ?? 0);
            var promptTokens = (direct?.PromptTokens ?? 0) + (crew?.PromptTokens ?? 0);
            var cachedTokens = (direct?.CachedTokens ?? 0) + (crew?.CachedTokens ?? 0);
            var reasoningTokens = (direct?.ReasoningTokens ?? 0) + (crew?.ReasoningTokens ?? 0);
            var completionTokens = (direct?.CompletionTokens ?? 0) + (crew?.CompletionTokens ?? 0);
            var totalCost = (direct?.Cost ?? 0m) + (crew?.Cost ?? 0m);

            return new ConversationUsageSummaryDto(
                c.Id,
                c.NotebookId,
                c.Title,
                c.Created,
                totalToolCalls,
                assistantSteps.GetValueOrDefault(c.Id, 0),
                promptTokens,
                cachedTokens,
                reasoningTokens,
                completionTokens,
                totalCost,
                turnIndicesByConversation.GetValueOrDefault(c.Id, Array.Empty<int>()));
        }).ToList();

        return new GuideUsageConversationsPageDto(page, pageSize, totalCount, items);
    }

    public async Task<GuideApiUsageReportDto?> GetGuideApiUsageReportAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to,
        string? sourceFilter = null)
    {
        var guide = await GetOwnedAssistantAsync(guideId);
        if (guide == null)
        {
            return null;
        }

        var normalizedSourceFilter = NormalizeSourceFilter(sourceFilter);
        IQueryable<UsageEvent> query = _context.UsageEvents
            .AsNoTracking()
            .Where(e => e.AssistantId == guideId
                     && e.ProjectId == projectId
                     && e.Created >= from
                     && e.Created <= to);

        query = ApplySourceFilter(query, normalizedSourceFilter);

        var slices = await query
            .Select(e => new ApiUsageSlice(
                e.SourceChannel,
                e.Operation,
                e.MetadataJson,
                e.ChargeUsd ?? 0m))
            .ToListAsync();

        var rows = slices
            .Select(MapApiUsageSlice)
            .GroupBy(
                x => new
                {
                    x.SourceChannel,
                    x.Endpoint,
                    x.Alias,
                    x.ProviderServiceMode,
                    x.StatusFamily
                })
            .Select(g => new GuideApiUsageRowDto(
                SourceChannel: g.Key.SourceChannel,
                Endpoint: g.Key.Endpoint,
                Alias: g.Key.Alias,
                ProviderServiceMode: g.Key.ProviderServiceMode,
                StatusFamily: g.Key.StatusFamily,
                Events: g.Count(),
                ChargeUsd: g.Sum(x => x.ChargeUsd)))
            .OrderByDescending(r => r.ChargeUsd)
            .ThenByDescending(r => r.Events)
            .ThenBy(r => r.SourceChannel)
            .ThenBy(r => r.Endpoint)
            .ToList();

        return new GuideApiUsageReportDto(
            GuideId: guide.Value.Id,
            GuideName: guide.Value.Name,
            FromDate: from,
            ToDate: to,
            SourceFilter: normalizedSourceFilter,
            TotalEvents: rows.Sum(r => r.Events),
            TotalChargeUsd: rows.Sum(r => r.ChargeUsd),
            Rows: rows);
    }

    public async Task<GuideUsageReportDto?> GetGuideUsageReportAsync(
        Guid projectId,
        Guid guideId,
        DateTime from,
        DateTime to)
    {
        // Validate guide exists
        var guide = await _context.Assistants
            .Where(a => a.Id == guideId)
            .Select(a => new { a.Id, a.Name })
            .FirstOrDefaultAsync();

        if (guide == null)
            return null;

        var overallSw = Stopwatch.StartNew();
        _logger?.LogInformation(
            "GuideUsage report start guideId={GuideId} projectId={ProjectId} from={From} to={To}",
            guideId,
            projectId,
            from,
            to);

        // =====================================================================
        // PHASE 1: Parallel independent queries using separate DbContext instances
        // =====================================================================
        
        var phase1Sw = Stopwatch.StartNew();

        // Each parallel query gets its own DbContext from the factory to avoid threading issues
        var crewMembersTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.Set<GuideMember>()
                .Where(gm => gm.GuideId == guideId)
                .Join(ctx.Assistants,
                    gm => gm.AssistantId,
                    a => a.Id,
                    (gm, a) => new { gm.AssistantId, a.Name, gm.DisplayOrder })
                .OrderBy(x => x.DisplayOrder == null ? 1 : 0)
                .ThenBy(x => x.DisplayOrder)
                .ThenBy(x => x.Name)
                .ToListAsync();
        });

        // CONSOLIDATED QUERY: All direct guide usage in one query, grouped by date/category/operation/conversation
        var directUsageDataTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId == null
                         && e.Created >= from
                         && e.Created <= to)
                .GroupBy(e => new { e.Created.Date, e.Category, e.Operation, e.ConversationId })
                .Select(g => new DirectUsageRow(
                    g.Key.Date,
                    g.Key.Category,
                    g.Key.Operation,
                    g.Key.ConversationId,
                    g.Count(),
                    g.Sum(e => e.ValueInput),
                    g.Sum(e => e.ValueCachedInput),
                    g.Sum(e => e.ValueReasoning),
                    g.Sum(e => e.ValueOutput),
                    g.Sum(e => e.ChargeUsd ?? 0m)))
                .ToListAsync();
        });

        // Direct tool call stats (exclude crew-bridge tool calls via metadata)
        var directToolCallsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId == null
                         && e.Category == UsageCategory.ToolCall
                         && e.Created >= from
                         && e.Created <= to
                         && (e.MetadataJson == null || !e.MetadataJson.Contains("\"assistantName\"")))
                .GroupBy(e => e.Operation)
                .Select(g => new { ToolName = g.Key, Count = g.Count(), TotalCost = g.Sum(e => e.ChargeUsd ?? 0m) })
                .ToListAsync();
        });

        // Conversation count query
        var totalConversationsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AssistantId == guideId
                         && e.ProjectId == projectId
                         && e.NotebookConversationMessageId != null
                         && e.Created >= from)
                .Select(e => e.ConversationId)
                .Distinct()
                .CountAsync();
        });

        // Crew usage totals (independent of invocations/messages)
        var crewUsageTotalsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.InvokingAssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId != null
                         && e.Created >= from
                         && e.Created <= to)
                .GroupBy(e => new { e.Created.Date, e.Category, e.ConversationId })
                .Select(g => new CrewUsageRow(
                    g.Key.Date,
                    g.Key.Category,
                    g.Key.ConversationId,
                    g.Count(),
                    g.Sum(e => e.ValueInput),
                    g.Sum(e => e.ValueCachedInput),
                    g.Sum(e => e.ValueReasoning),
                    g.Sum(e => e.ValueOutput),
                    g.Sum(e => e.ChargeUsd ?? 0m)))
                .ToListAsync();
        });

        // Crew member usage totals (by invoked assistant)
        var crewMemberTotalsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.InvokingAssistantId == guideId
                         && e.ProjectId == projectId
                         && e.AgentInvocationId != null
                         && e.AssistantId != null
                         && e.Created >= from
                         && e.Created <= to)
                .GroupBy(e => e.AssistantId)
                .Select(g => new
                {
                    AssistantId = g.Key!.Value,
                    InvocationCount = g.Select(e => e.AgentInvocationId).Distinct().Count(),
                    TotalCost = g.Sum(e => e.ChargeUsd ?? 0m)
                })
                .ToListAsync();
        });

        // Await all Phase 1 queries in parallel
        await Task.WhenAll(
            crewMembersTask,
            directUsageDataTask,
            directToolCallsTask,
            totalConversationsTask,
            crewUsageTotalsTask,
            crewMemberTotalsTask);

        var crewMembers = await crewMembersTask;
        var directUsageData = await directUsageDataTask;
        var directToolCalls = await directToolCallsTask;
        var totalConversations = await totalConversationsTask;
        var crewUsageTotals = await crewUsageTotalsTask;
        var crewMemberTotals = await crewMemberTotalsTask;

        phase1Sw.Stop();
        _logger?.LogInformation(
            "GuideUsage phase1 complete in {ElapsedMs}ms: crewMembers={CrewMembers} directUsageRows={DirectUsageRows} toolCalls={ToolCalls} totalConversations={TotalConversations} crewUsageRows={CrewUsageRows} crewMemberTotals={CrewMemberTotals}",
            phase1Sw.ElapsedMilliseconds,
            crewMembers.Count,
            directUsageData.Count,
            directToolCalls.Count,
            totalConversations,
            crewUsageTotals.Count,
            crewMemberTotals.Count);

        // =====================================================================
        // Derive aggregates from consolidated direct usage data (in-memory)
        // =====================================================================
        
        // Total tokens and cost (replaces Q1)
        var chatCompletionRows = directUsageData.Where(r => r.Category == UsageCategory.ChatCompletion).ToList();
        var totalPromptTokens = chatCompletionRows.Sum(r => r.PromptTokens);
        var totalCachedTokens = chatCompletionRows.Sum(r => r.CachedTokens);
        var totalReasoningTokens = chatCompletionRows.Sum(r => r.ReasoningTokens);
        var totalCompletionTokens = chatCompletionRows.Sum(r => r.CompletionTokens);
        var directCost = directUsageData.Sum(r => r.Cost);

        // Tool call data (replaces Q2)
        var toolCallData = directToolCalls;

        // Direct daily buckets (replaces Q8 direct part)
        var directDailyBuckets = directUsageData
            .GroupBy(r => r.Date)
            .Select(g => new {
                Date = g.Key,
                PromptTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.PromptTokens),
                CachedTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CachedTokens),
                ReasoningTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.ReasoningTokens),
                CompletionTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CompletionTokens),
                Cost = g.Sum(r => r.Cost)
            })
            .ToList();

        // Direct conversation usage (replaces Q7b direct part)
        var directConversationUsage = directUsageData
            .Where(r => r.ConversationId != null)
            .GroupBy(r => r.ConversationId!.Value)
            .Select(g => new ConversationAggregate(
                g.Key,
                g.Where(r => r.Category == UsageCategory.ToolCall).Sum(r => r.Count),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.PromptTokens),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CachedTokens),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.ReasoningTokens),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CompletionTokens),
                g.Sum(r => r.Cost)))
            .ToList();

        // =====================================================================
        // Crew usage queries (independent of invocations/messages)
        // =====================================================================

        var crewUsageData = crewUsageTotals;

        // Derive crew aggregates from consolidated data (in-memory)
        var crewChatRows = crewUsageData.Where(r => r.Category == UsageCategory.ChatCompletion).ToList();
        
        // Crew token totals (replaces Q7)
        var crewPromptTokens = crewChatRows.Sum(r => r.PromptTokens);
        var crewCachedTokens = crewChatRows.Sum(r => r.CachedTokens);
        var crewReasoningTokens = crewChatRows.Sum(r => r.ReasoningTokens);
        var crewCompletionTokens = crewChatRows.Sum(r => r.CompletionTokens);

        var totalPromptTokensWithCrew = totalPromptTokens + crewPromptTokens;
        var totalCachedTokensWithCrew = totalCachedTokens + crewCachedTokens;
        var totalReasoningTokensWithCrew = totalReasoningTokens + crewReasoningTokens;
        var totalCompletionTokensWithCrew = totalCompletionTokens + crewCompletionTokens;

        // Crew conversation usage (replaces Q7b crew part)
        var crewConversationUsage = crewUsageData
            .Where(r => r.ConversationId != null)
            .GroupBy(r => r.ConversationId!.Value)
            .Select(g => new ConversationAggregate(
                g.Key,
                g.Where(r => r.Category == UsageCategory.ToolCall).Sum(r => r.Count),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.PromptTokens),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CachedTokens),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.ReasoningTokens),
                g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CompletionTokens),
                g.Sum(r => r.Cost)))
            .ToList();

        // Crew daily buckets (replaces Q8 crew part)
        var crewDailyBuckets = crewUsageData
            .GroupBy(r => r.Date)
            .Select(g => new {
                Date = g.Key,
                PromptTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.PromptTokens),
                CachedTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CachedTokens),
                ReasoningTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.ReasoningTokens),
                CompletionTokens = g.Where(r => r.Category == UsageCategory.ChatCompletion).Sum(r => r.CompletionTokens),
                Cost = g.Sum(r => r.Cost)
            })
            .ToList();

        // =====================================================================
        // PHASE 4: Conversation metadata queries (run in parallel)
        // =====================================================================
        
        var conversationIdSet = directConversationUsage
            .Select(c => c.ConversationId)
            .Union(crewConversationUsage.Select(c => c.ConversationId))
            .Distinct()
            .ToList();

        // Run all conversation metadata queries in parallel using separate DbContext instances
        var conversationMetadataTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.NotebookConversations
                .Where(c => conversationIdSet.Contains(c.Id))
                .Select(c => new { c.Id, c.NotebookId, c.Title, c.Created })
                .ToListAsync();
        });

        var assistantStepsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.NotebookConversationMessages
                .Where(m => conversationIdSet.Contains(m.NotebookConversationId)
                            && m.AssistantId == guideId
                            && m.Role == ChatRole.Assistant)
                .GroupBy(m => m.NotebookConversationId)
                .Select(g => new { ConversationId = g.Key, Steps = g.Count() })
                .ToDictionaryAsync(x => x.ConversationId, x => x.Steps);
        });

        var turnIndicesByConversationTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.ConversationTurns
                .Where(t => conversationIdSet.Contains(t.NotebookConversationId))
                .GroupBy(t => t.NotebookConversationId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => (IReadOnlyList<int>)g.Select(t => t.TurnIndex).Distinct().OrderBy(i => i).ToList());
        });

        // Await all Phase 4 queries in parallel
        await Task.WhenAll(conversationMetadataTask, assistantStepsTask, turnIndicesByConversationTask);

        var conversationMetadata = await conversationMetadataTask;
        var assistantSteps = await assistantStepsTask;
        var turnIndicesByConversation = await turnIndicesByConversationTask;

        _logger?.LogInformation(
            "GuideUsage phase4 conversation metadata: conversations={ConversationCount} assistantSteps={AssistantSteps} turnIndexSets={TurnIndexSets}",
            conversationMetadata.Count,
            assistantSteps.Count,
            turnIndicesByConversation.Count);

        // Assistants that "belong" to this guide for drill-downs (guide + crew)
        var allowedAssistantIds = new HashSet<Guid> { guideId };
        foreach (var cm in crewMembers)
        {
            allowedAssistantIds.Add(cm.AssistantId);
        }

        var directByConversation = directConversationUsage.ToDictionary(c => c.ConversationId);
        var crewByConversation = crewConversationUsage.ToDictionary(c => c.ConversationId);

        var conversationSummaries = conversationMetadata
            .Select(c =>
            {
                directByConversation.TryGetValue(c.Id, out var direct);
                crewByConversation.TryGetValue(c.Id, out var crew);

                var totalToolCalls = (direct?.ToolCalls ?? 0) + (crew?.ToolCalls ?? 0);
                var promptTokens = (direct?.PromptTokens ?? 0) + (crew?.PromptTokens ?? 0);
                var cachedTokens = (direct?.CachedTokens ?? 0) + (crew?.CachedTokens ?? 0);
                var reasoningTokens = (direct?.ReasoningTokens ?? 0) + (crew?.ReasoningTokens ?? 0);
                var completionTokens = (direct?.CompletionTokens ?? 0) + (crew?.CompletionTokens ?? 0);
                var totalCostForConversation = (direct?.Cost ?? 0m) + (crew?.Cost ?? 0m);
                var steps = assistantSteps.GetValueOrDefault(c.Id, 0);
                turnIndicesByConversation.TryGetValue(c.Id, out var turnIndices);

                return new ConversationUsageSummaryDto(
                    c.Id,
                    c.NotebookId,
                    c.Title,
                    c.Created,
                    totalToolCalls,
                    steps,
                    promptTokens,
                    cachedTokens,
                    reasoningTokens,
                    completionTokens,
                    totalCostForConversation,
                    turnIndices ?? Array.Empty<int>());
            })
            .OrderByDescending(c => c.Created)
            .ToList();

        // Build crew member DTOs with full subtree costs
        var crewTotalsByAssistant = crewMemberTotals.ToDictionary(x => x.AssistantId, x => x);
        var crewMemberDtos = crewMembers.Select(cm =>
        {
            crewTotalsByAssistant.TryGetValue(cm.AssistantId, out var totals);
            var invocationCount = totals?.InvocationCount ?? 0;
            var totalCostForMember = totals?.TotalCost ?? 0m;
            var avgInvocations = totalConversations > 0 ? (double)invocationCount / totalConversations : 0.0;
            var avgCost = totalConversations > 0 ? totalCostForMember / totalConversations : 0m;

            return new CrewMemberUsageDto(cm.AssistantId, cm.Name, invocationCount, avgInvocations, totalCostForMember, avgCost);
        }).ToList();

        // Build tool usage DTOs
        var toolUsageDtos = toolCallData.OrderBy(t => t.ToolName).Select(tool =>
        {
            var avgCalls = totalConversations > 0 ? (double)tool.Count / totalConversations : 0.0;
            var avgCost = totalConversations > 0 ? tool.TotalCost / totalConversations : 0m;
            return new ToolUsageDto(tool.ToolName ?? "unknown", tool.Count, avgCalls, tool.TotalCost, avgCost);
        }).ToList();

        // === Calculate crew cost from usage totals ===
        var crewCost = crewUsageTotals.Sum(r => r.Cost);
        var totalCost = directCost + crewCost;

        // Merge daily buckets
        var crewByDate = crewDailyBuckets.ToDictionary(
            x => x.Date,
            x => new
            {
                x.PromptTokens,
                x.CachedTokens,
                x.ReasoningTokens,
                x.CompletionTokens,
                x.Cost
            });

        var dailyBuckets = directDailyBuckets
            .Select(d =>
            {
                crewByDate.TryGetValue(d.Date, out var crew);
                var totalPrompt = d.PromptTokens + (crew?.PromptTokens ?? 0);
                var totalCached = d.CachedTokens + (crew?.CachedTokens ?? 0);
                var totalReasoning = d.ReasoningTokens + (crew?.ReasoningTokens ?? 0);
                var totalCompletion = d.CompletionTokens + (crew?.CompletionTokens ?? 0);
                var directCostForDay = d.Cost;
                var totalCostForDay = d.Cost + (crew?.Cost ?? 0m);

                return new DailyUsageBucketDto(
                    d.Date,
                    d.PromptTokens,
                    d.CachedTokens,
                    d.ReasoningTokens,
                    d.CompletionTokens,
                    totalPrompt,
                    totalCached,
                    totalReasoning,
                    totalCompletion,
                    directCostForDay,
                    totalCostForDay);
            })
            .OrderBy(d => d.Date)
            .ToList();

        // Add any crew-only days not in direct buckets
        foreach (var crewDay in crewDailyBuckets.Where(c => !directDailyBuckets.Any(d => d.Date == c.Date)))
        {
            dailyBuckets.Add(new DailyUsageBucketDto(
                crewDay.Date,
                0,
                0,
                0,
                0,
                crewDay.PromptTokens,
                crewDay.CachedTokens,
                crewDay.ReasoningTokens,
                crewDay.CompletionTokens,
                0m,
                crewDay.Cost));
        }
        dailyBuckets = dailyBuckets.OrderBy(d => d.Date).ToList();

        // Debug logging for token aggregation issues (safe and lightweight)
        try
        {
            _logger?.LogInformation(
                "GuideUsage dailyBuckets for Guide {GuideId} between {From} and {To}: Count={Count}, " +
                "DirectTokens={DirectTokens}, TotalTokensWithCrew={TotalTokensWithCrew}, " +
                "DirectCost={DirectCost}, TotalCost={TotalCost}, Conversations={Conversations}",
                guideId,
                from,
                to,
                dailyBuckets.Count,
                dailyBuckets.Sum(b => b.PromptTokens + b.CachedTokens + b.ReasoningTokens + b.CompletionTokens),
                dailyBuckets.Sum(b => b.PromptTokensWithCrew + b.CachedTokensWithCrew + b.ReasoningTokensWithCrew + b.CompletionTokensWithCrew),
                dailyBuckets.Sum(b => b.DirectCost),
                dailyBuckets.Sum(b => b.TotalCost),
                conversationSummaries.Count);
        }
        catch
        {
            // Never let diagnostics break the report
        }

        var report = new GuideUsageReportDto(
            guideId,
            guide.Name,
            from,
            to,
            totalConversations,
            totalPromptTokens,
            totalCachedTokens,
            totalReasoningTokens,
            totalCompletionTokens,
            totalPromptTokensWithCrew,
            totalCachedTokensWithCrew,
            totalReasoningTokensWithCrew,
            totalCompletionTokensWithCrew,
            directCost,
            totalCost,
            crewMemberDtos,
            toolUsageDtos,
            dailyBuckets,
            conversationSummaries
        );

        overallSw.Stop();
        _logger?.LogInformation(
            "GuideUsage report complete in {ElapsedMs}ms guideId={GuideId} projectId={ProjectId}",
            overallSw.ElapsedMilliseconds,
            guideId,
            projectId);

        return report;
    }

    public async Task<InvocationNodeDto?> GetInvocationAsync(
        Guid invocationId,
        bool includeMessages = true)
    {
        // First, try to find as an AgentInvocation
        var invocation = await _context.AgentInvocations
            .Include(i => i.ParentConversation)
            .Include(i => i.ChildInvocations)
            .Where(i => i.Id == invocationId)
            .FirstOrDefaultAsync();

        if (invocation != null)
        {
            // Found as AgentInvocation - return its details
            List<InvocationMessageDto>? messages = null;
            if (includeMessages)
            {
                messages = await _context.AgentInvocationMessages
                    .Where(m => m.AgentInvocationId == invocationId)
                    .OrderBy(m => m.Sequence)
                    .Select(m => new InvocationMessageDto(
                        m.Id,
                        m.Role.ToString(),
                        m.Content,
                        m.ToolCallsJson,
                        m.FunctionName,
                        m.Created
                    ))
                    .ToListAsync();
            }

            return await BuildInvocationNodeAsync(invocation, includeMessages, messages);
        }

        // Not found as AgentInvocation - try as ConversationId
        // This handles the case where the guide usage report uses conversation-level summaries
        var conversation = await _context.NotebookConversations
            .Include(c => c.Notebook)
            .Where(c => c.Id == invocationId)
            .FirstOrDefaultAsync();

        if (conversation == null)
            return null;

        // Get usage summary for this conversation
        var usageEvents = await _context.UsageEvents
            .Where(e => e.ConversationId == invocationId)
            .ToListAsync();

        var promptTokens = usageEvents.Sum(e => e.ValueInput);
        var completionTokens = usageEvents.Sum(e => e.ValueOutput);
        var chargeUsd = usageEvents.Sum(e => e.ChargeUsd ?? 0m);
        var eventCount = usageEvents.Count;
        
        // Calculate duration from event timestamps
        long durationMs = 0;
        if (usageEvents.Count > 0)
        {
            var firstEvent = usageEvents.Min(e => e.Created);
            var lastEvent = usageEvents.Max(e => e.Created);
            durationMs = (long)(lastEvent - firstEvent).TotalMilliseconds;
        }

        // Get any agent invocations for this conversation as children
        var childInvocations = await _context.AgentInvocations
            .Where(i => i.ParentConversationId == invocationId
                        && !ExcludedAssistantNames.Contains(i.AssistantName))
            .OrderBy(i => i.Created)
            .ToListAsync();

        var convoInvocationIds = childInvocations.Select(i => i.Id).ToList();

        // Tool usage events grouped by AgentInvocationId so we can attach tool nodes
        // under each invocation in the tree.
        var toolUsageByInvocation = await _context.UsageEvents
            .Where(e => e.AgentInvocationId != null
                        && convoInvocationIds.Contains(e.AgentInvocationId.Value)
                        && e.Category == UsageCategory.ToolCall)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.ToList());

        // Token usage per invocation for this conversation (guide-agnostic)
        var tokenUsageByInvocation = await _context.UsageEvents
            .Where(e => e.AgentInvocationId != null
                        && convoInvocationIds.Contains(e.AgentInvocationId.Value)
                        && e.Category == UsageCategory.ChatCompletion)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionaryAsync(
                g => g.Key,
                g => new InvocationTokenAggregate(
                    g.Sum(e => e.ValueInput),
                    g.Sum(e => e.ValueOutput)));

        // Combine counts: usage events + ToolCallCount stored on invocations that may not write events
        var usageToolCalls = await _context.UsageEvents
            .Where(e => e.ConversationId == invocationId && e.Category == UsageCategory.ToolCall)
            .CountAsync();

        var invocationToolCalls = childInvocations.Sum(i => i.ToolCallCount);

        var totalToolCalls = usageToolCalls + invocationToolCalls;

        var children = new List<InvocationNodeDto>();
        foreach (var child in childInvocations.Where(i => i.ParentInvocationId == null))
        {
            var childNode = await BuildInvocationTreeAsync(
                child,
                childInvocations,
                toolUsageByInvocation,
                tokenUsageByInvocation);
            children.Add(childNode);
        }

        // Return a node representing the conversation
        return new InvocationNodeDto(
            invocationId,
            null,
            null,
            conversation.Notebook.Title ?? "Conversation",
            null,
            null,
            0,
            "completed",
            promptTokens,
            completionTokens,
            totalToolCalls,
            eventCount,
            chargeUsd,
            conversation.Created,
            usageEvents.Count > 0 ? usageEvents.Max(e => e.Created) : null,
            durationMs,
            false,
            children,
            null
        );
    }

    public async Task<TurnInvocationTreeDto?> GetTurnInvocationsAsync(
        Guid conversationId,
        int turnIndex)
    {
        // Get all invocations for this specific turn
        var allInvocations = await _context.AgentInvocations
            .Include(i => i.ChildInvocations)
            .Where(i => i.ParentConversationId == conversationId
                        && i.ParentTurnIndex == turnIndex
                        && !ExcludedAssistantNames.Contains(i.AssistantName))
            .OrderBy(i => i.Created)
            .ToListAsync();

        // If no agent invocations, check for root-level usage (published guides with direct tool calls)
        if (!allInvocations.Any())
        {
            return await BuildRootLevelTurnTreeAsync(conversationId, turnIndex);
        }

        var invocationIds = allInvocations.Select(i => i.Id).ToList();
        
        // Build triggering tool call IDs set for later filtering
        var triggeringToolCallIds = allInvocations
            .Where(i => i.TriggeringToolCallId != null)
            .Select(i => i.TriggeringToolCallId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // =====================================================================
        // Run all dependent queries in parallel using separate DbContext instances
        // =====================================================================

        // Query 1: Messages for invocations
        var messagesTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.AgentInvocationMessages
                .Where(m => invocationIds.Contains(m.AgentInvocationId))
                .OrderBy(m => m.Sequence)
                .Select(m => new
                {
                    m.AgentInvocationId,
                    Dto = new InvocationMessageDto(
                        m.Id,
                        m.Role.ToString(),
                        m.Content,
                        m.ToolCallsJson,
                        m.FunctionName,
                        m.Created)
                })
                .GroupBy(x => x.AgentInvocationId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.Select(x => x.Dto).ToList());
        });

        // Query 2: CONSOLIDATED UsageEvents query - get all usage data in one query
        var usageEventsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AgentInvocationId != null
                            && invocationIds.Contains(e.AgentInvocationId.Value))
                .Select(e => new
                {
                    e.AgentInvocationId,
                    e.Category,
                    e.Operation,
                    e.ValueInput,
                    e.ValueOutput,
                    e.ChargeUsd,
                    e.Created
                })
                .ToListAsync();
        });

        // Query 3: Direct tool events (not triggered by agent invocations)
        var directToolEventsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.NotebookConversationMessageId != null
                         && e.Category == UsageCategory.ToolCall)
                .Join(
                    ctx.NotebookConversationMessages
                        .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turnIndex),
                    e => e.NotebookConversationMessageId,
                    m => m.Id,
                    (e, m) => new { Event = e, ToolCallId = m.ToolCallId })
                .Where(x => x.ToolCallId == null || !triggeringToolCallIds.Contains(x.ToolCallId))
                .OrderBy(x => x.Event.Created)
                .ToListAsync();
        });

        // Await all queries in parallel
        await Task.WhenAll(messagesTask, usageEventsTask, directToolEventsTask);

        var messagesByInvocation = await messagesTask;
        var allUsageEvents = await usageEventsTask;
        var directToolEvents = await directToolEventsTask;

        // =====================================================================
        // Derive all usage aggregates from consolidated query (in-memory)
        // =====================================================================

        // Tool usage by invocation
        var toolUsageByInvocation = allUsageEvents
            .Where(e => e.Category == UsageCategory.ToolCall)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new UsageEvent
                {
                    AgentInvocationId = e.AgentInvocationId,
                    Category = e.Category,
                    Operation = e.Operation ?? string.Empty,
                    ChargeUsd = e.ChargeUsd,
                    Created = e.Created
                }).ToList());

        // Service usage by invocation (non-Chat, non-Tool)
        var serviceUsageByInvocation = allUsageEvents
            .Where(e => e.Category != UsageCategory.ChatCompletion && e.Category != UsageCategory.ToolCall)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new UsageEvent
                {
                    AgentInvocationId = e.AgentInvocationId,
                    Category = e.Category,
                    Operation = e.Operation ?? string.Empty,
                    ChargeUsd = e.ChargeUsd,
                    Created = e.Created
                }).ToList());

        // Token usage by invocation
        var tokenUsageByInvocation = allUsageEvents
            .Where(e => e.Category == UsageCategory.ChatCompletion)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new InvocationTokenAggregate(
                    g.Sum(e => e.ValueInput),
                    g.Sum(e => e.ValueOutput)));

        // Cost per invocation
        var invocationCosts = allUsageEvents
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(e => e.ChargeUsd ?? 0m));

        // Build tree from root invocations (no parent)
        var rootInvocations = allInvocations.Where(i => i.ParentInvocationId == null).ToList();
        var rootNodes = new List<InvocationNodeDto>();

        foreach (var root in rootInvocations)
        {
            var node = BuildInvocationTree(
                root,
                allInvocations,
                toolUsageByInvocation,
                serviceUsageByInvocation,
                tokenUsageByInvocation,
                messagesByInvocation,
                invocationCosts);
            rootNodes.Add(node);
        }

        foreach (var item in directToolEvents)
        {
            // Suppress ReadWeb bridge tool rows - these do not represent billable usage
            // and would confuse the usage guideants. The underlying crawl usage is
            // represented separately.
            if (string.Equals(item.Event.Operation, "ReadWeb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rootNodes.Add(new InvocationNodeDto(
                item.Event.Id,
                null,
                null,
                $"Tool: {item.Event.Operation}",
                null,
                null,
                0,
                "completed",
                0,
                0,
                0,
                0,
                item.Event.ChargeUsd ?? 0m,
                item.Event.Created,
                item.Event.Created,
                0,
                false,
                new List<InvocationNodeDto>(),
                null
            ));
        }

        // Sort all root nodes by created time
        rootNodes = rootNodes.OrderBy(n => n.Created).ToList();

        var turnStarted = allInvocations.Min(i => i.Created);
        var totalDuration = allInvocations.Max(i => i.DurationMs ?? 0);
        // Use UsageEvents-based token aggregates rather than UsageJson
        var totalTokens = tokenUsageByInvocation.Values.Sum(a => a.PromptTokens + a.CompletionTokens);
        
        // Calculate total charge from this turn's invocations plus direct tool events
        var invocationCharges = invocationCosts.Values.Sum();
        var directToolCharges = directToolEvents.Sum(x => x.Event.ChargeUsd ?? 0m);
        var totalCharge = invocationCharges + directToolCharges;

        return new TurnInvocationTreeDto(
            conversationId,
            turnIndex,
            turnStarted,
            totalDuration,
            totalCharge,
            totalTokens,
            rootNodes
        );
    }

    /// <summary>
    /// Synchronous tree builder that works with pre-loaded data (no async DB calls).
    /// </summary>
    private InvocationNodeDto BuildInvocationTree(
        AgentInvocation invocation,
        List<AgentInvocation> allInvocations,
        Dictionary<Guid, List<UsageEvent>> toolUsageByInvocation,
        Dictionary<Guid, List<UsageEvent>> serviceUsageByInvocation,
        Dictionary<Guid, InvocationTokenAggregate> tokenUsageByInvocation,
        Dictionary<Guid, List<InvocationMessageDto>> messagesByInvocation,
        Dictionary<Guid, decimal> invocationCosts)
    {
        var tokens = tokenUsageByInvocation.GetValueOrDefault(invocation.Id);
        var promptTokens = tokens?.PromptTokens ?? 0;
        var completionTokens = tokens?.CompletionTokens ?? 0;
        var chargeUsd = invocationCosts.GetValueOrDefault(invocation.Id, 0m);
        messagesByInvocation.TryGetValue(invocation.Id, out var messages);

        // Build child nodes
        var children = new List<InvocationNodeDto>();

        // Add tool usage nodes as children
        if (toolUsageByInvocation.TryGetValue(invocation.Id, out var toolUsage))
        {
            foreach (var tool in toolUsage.OrderBy(t => t.Created))
            {
                // Suppress ReadWeb bridge tool rows - these do not represent billable usage
                // and would confuse the usage guideants. The underlying crawl usage is
                // represented separately.
                if (string.Equals(tool.Operation, "ReadWeb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                children.Add(new InvocationNodeDto(
                    tool.Id,
                    invocation.Id,
                    null,
                    $"Tool: {tool.Operation}",
                    null,
                    null,
                    invocation.Depth + 1,
                    "completed",
                    0,
                    0,
                    0,
                    0,
                    tool.ChargeUsd ?? 0m,
                    tool.Created,
                    tool.Created,
                    0,
                    false,
                    new List<InvocationNodeDto>(),
                    null
                ));
            }
        }

        // Add service usage nodes as children
        if (serviceUsageByInvocation.TryGetValue(invocation.Id, out var serviceUsage))
        {
            foreach (var svc in serviceUsage.OrderBy(s => s.Created))
            {
                children.Add(new InvocationNodeDto(
                    svc.Id,
                    invocation.Id,
                    null,
                    svc.Operation,
                    null,
                    null,
                    invocation.Depth + 1,
                    "completed",
                    0,
                    0,
                    0,
                    0,
                    svc.ChargeUsd ?? 0m,
                    svc.Created,
                    svc.Created,
                    0,
                    false,
                    new List<InvocationNodeDto>(),
                    null
                ));
            }
        }

        // Add child invocations recursively
        var childInvocations = allInvocations.Where(i => i.ParentInvocationId == invocation.Id).ToList();
        foreach (var child in childInvocations.OrderBy(c => c.Created))
        {
            children.Add(BuildInvocationTree(
                child,
                allInvocations,
                toolUsageByInvocation,
                serviceUsageByInvocation,
                tokenUsageByInvocation,
                messagesByInvocation,
                invocationCosts));
        }

        return new InvocationNodeDto(
            invocation.Id,
            invocation.ParentInvocationId,
            invocation.TriggeringToolCallId,
            invocation.AssistantName,
            invocation.AssistantId,
            invocation.ModelDeploymentId,
            invocation.Depth,
            invocation.Status,
            promptTokens,
            completionTokens,
            invocation.ToolCallCount,
            invocation.LlmRoundTrips,
            chargeUsd,
            invocation.Created,
            invocation.Completed,
            invocation.DurationMs ?? 0,
            false,
            children.OrderBy(c => c.Created).ToList(),
            messages
        );
    }

    /// <summary>
    /// Builds a turn tree for conversations without agent invocations (e.g., published guides with direct tool calls).
    /// Creates a synthetic root node representing the guide itself with tool usage as children.
    /// </summary>
    private async Task<TurnInvocationTreeDto?> BuildRootLevelTurnTreeAsync(Guid conversationId, int turnIndex)
    {
        // Get conversation and turn details
        var conversation = await _context.NotebookConversations
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Guide)
            .Include(c => c.Turns)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null) return null;

        var turn = conversation.Turns.FirstOrDefault(t => t.TurnIndex == turnIndex);
        if (turn == null) return null;

        // Get all usage events for this conversation's turn (by message turn index)
        var turnMessages = await _context.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turnIndex)
            .Select(m => m.Id)
            .ToListAsync();

        if (!turnMessages.Any()) return null;

        // Get usage events linked to these messages
        var usageEvents = await _context.UsageEvents
            .Where(e => e.NotebookConversationMessageId != null
                     && turnMessages.Contains(e.NotebookConversationMessageId.Value))
            .ToListAsync();

        if (!usageEvents.Any()) return null;

        // Aggregate metrics
        var chatEvents = usageEvents.Where(e => e.Category == UsageCategory.ChatCompletion).ToList();
        var toolEvents = usageEvents.Where(e => e.Category == UsageCategory.ToolCall).ToList();
        var serviceEvents = usageEvents.Where(e => e.Category != UsageCategory.ChatCompletion && e.Category != UsageCategory.ToolCall).ToList();

        var promptTokens = chatEvents.Sum(e => e.ValueInput);
        var completionTokens = chatEvents.Sum(e => e.ValueOutput);
        var totalCharge = usageEvents.Sum(e => e.ChargeUsd ?? 0m);
        var totalTokens = promptTokens + completionTokens;

        // Build tool nodes as root-level items (the card is the root, tools are direct children)
        var toolNodes = toolEvents
            // Suppress ReadWeb bridge tool rows - these do not represent billable usage
            // and would confuse the usage guideants. The underlying crawl usage is
            // represented separately.
            .Where(e => !string.Equals(e.Operation, "ReadWeb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Created)
            .Select(e => new InvocationNodeDto(
                e.Id,                           // Use usage event ID as node ID
                null,                           // No parent invocation
                null,                           // No triggering tool call
                $"Tool: {e.Operation}",         // Display as "Tool: FunctionName"
                null,                           // No assistant ID (it's a tool, not an agent)
                null,                           // No model
                0,                              // Depth 0 (root level)
                "completed",
                0,                              // Tools don't have prompt tokens
                0,                              // Tools don't have completion tokens
                0,                              // Tool call count
                0,                              // LLM round trips
                e.ChargeUsd ?? 0m,
                e.Created,
                e.Created,
                0,                              // Duration
                false,                          // Not outlier
                new List<InvocationNodeDto>(),  // No children
                null                            // No messages
            ))
            .ToList();

        // Build AI service nodes as root-level items (image gen, TTS, etc.)
        var serviceNodes = serviceEvents
            .OrderBy(e => e.Created)
            .Select(e => new InvocationNodeDto(
                e.Id,
                null,
                null,
                e.Operation,                    // Display operation name directly
                null,                           // No assistant ID (AI service node)
                null,
                0,                              // Depth 0 (root level)
                "completed",
                0,
                0,
                0,
                0,
                e.ChargeUsd ?? 0m,
                e.Created,
                e.Created,
                0,
                false,
                new List<InvocationNodeDto>(),
                null
            ))
            .ToList();

        // All items are root-level - the card itself is the container
        var rootNodes = toolNodes.Concat(serviceNodes).OrderBy(n => n.Created).ToList();

        return new TurnInvocationTreeDto(
            conversationId,
            turnIndex,
            turn.Created,
            (long?)((turn.LastUpdated - turn.Created).TotalMilliseconds) ?? 0,
            totalCharge,
            totalTokens,
            rootNodes
        );
    }

    /// <summary>
    /// Gets all turn invocation trees for a conversation in a single optimized query.
    /// </summary>
    public async Task<List<TurnInvocationTreeDto>> GetAllTurnInvocationsAsync(
        Guid conversationId)
    {
        // Get all turn indices for this conversation
        var turnIndices = await _context.ConversationTurns
            .Where(t => t.NotebookConversationId == conversationId)
            .Select(t => t.TurnIndex)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();

        if (!turnIndices.Any())
        {
            return new List<TurnInvocationTreeDto>();
        }

        // Get ALL invocations for this conversation (all turns at once)
        var allInvocations = await _context.AgentInvocations
            .Include(i => i.ChildInvocations)
            .Where(i => i.ParentConversationId == conversationId
                        && !ExcludedAssistantNames.Contains(i.AssistantName))
            .OrderBy(i => i.Created)
            .ToListAsync();

        var invocationIds = allInvocations.Select(i => i.Id).ToList();
        
        // Build triggering tool call IDs set
        var triggeringToolCallIds = allInvocations
            .Where(i => i.TriggeringToolCallId != null)
            .Select(i => i.TriggeringToolCallId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // =====================================================================
        // Run all queries in parallel using separate DbContext instances
        // =====================================================================

        var messagesTask = Task.Run(async () =>
        {
            if (!invocationIds.Any()) return new Dictionary<Guid, List<InvocationMessageDto>>();
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.AgentInvocationMessages
                .Where(m => invocationIds.Contains(m.AgentInvocationId))
                .OrderBy(m => m.Sequence)
                .Select(m => new
                {
                    m.AgentInvocationId,
                    Dto = new InvocationMessageDto(
                        m.Id,
                        m.Role.ToString(),
                        m.Content,
                        m.ToolCallsJson,
                        m.FunctionName,
                        m.Created)
                })
                .GroupBy(x => x.AgentInvocationId)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.Select(x => x.Dto).ToList());
        });

        var usageEventsTask = Task.Run(async () =>
        {
            if (!invocationIds.Any()) return new List<UsageEvent>();
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.AgentInvocationId != null
                            && invocationIds.Contains(e.AgentInvocationId.Value))
                .ToListAsync();
        });

        var directToolEventsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.UsageEvents
                .Where(e => e.NotebookConversationMessageId != null
                         && e.Category == UsageCategory.ToolCall)
                .Join(
                    ctx.NotebookConversationMessages
                        .Where(m => m.NotebookConversationId == conversationId),
                    e => e.NotebookConversationMessageId,
                    m => m.Id,
                    (e, m) => new { Event = e, m.ToolCallId, m.TurnIndex })
                .Where(x => x.ToolCallId == null || !triggeringToolCallIds.Contains(x.ToolCallId))
                .OrderBy(x => x.Event.Created)
                .ToListAsync();
        });

        var turnsTask = Task.Run(async () =>
        {
            await using var ctx = await _contextFactory.CreateDbContextAsync();
            return await ctx.ConversationTurns
                .Where(t => t.NotebookConversationId == conversationId)
                .ToDictionaryAsync(t => t.TurnIndex, t => t);
        });

        await Task.WhenAll(messagesTask, usageEventsTask, directToolEventsTask, turnsTask);

        var messagesByInvocation = await messagesTask;
        var allUsageEvents = await usageEventsTask;
        var allDirectToolEvents = await directToolEventsTask;
        var turnsByIndex = await turnsTask;

        // =====================================================================
        // Build aggregates from usage events (in-memory)
        // =====================================================================

        var toolUsageByInvocation = allUsageEvents
            .Where(e => e.Category == UsageCategory.ToolCall)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var serviceUsageByInvocation = allUsageEvents
            .Where(e => e.Category != UsageCategory.ChatCompletion && e.Category != UsageCategory.ToolCall)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var tokenUsageByInvocation = allUsageEvents
            .Where(e => e.Category == UsageCategory.ChatCompletion)
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new InvocationTokenAggregate(
                    g.Sum(e => e.ValueInput),
                    g.Sum(e => e.ValueOutput)));

        var invocationCosts = allUsageEvents
            .GroupBy(e => e.AgentInvocationId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.ChargeUsd ?? 0m));

        // Group direct tool events by turn
        var directToolEventsByTurn = allDirectToolEvents
            .GroupBy(x => x.TurnIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        // =====================================================================
        // Build turn trees
        // =====================================================================

        var results = new List<TurnInvocationTreeDto>();

        foreach (var turnIndex in turnIndices)
        {
            var turnInvocations = allInvocations.Where(i => i.ParentTurnIndex == turnIndex).ToList();

            if (!turnInvocations.Any())
            {
                // Check for root-level usage (published guides with direct tool calls)
                if (directToolEventsByTurn.TryGetValue(turnIndex, out var directTools) && directTools.Any())
                {
                    var rootNodes = directTools
                        // Suppress ReadWeb bridge tool rows - these do not represent billable usage
                        // and would confuse the usage guideants. The underlying crawl usage is
                        // represented separately.
                        .Where(x => !string.Equals(x.Event.Operation, "ReadWeb", StringComparison.OrdinalIgnoreCase))
                        .Select(x => new InvocationNodeDto(
                            x.Event.Id,
                            null,
                            null,
                            $"Tool: {x.Event.Operation}",
                            null,
                            null,
                            0,
                            "completed",
                            0, 0, 0, 0,
                            x.Event.ChargeUsd ?? 0m,
                            x.Event.Created,
                            x.Event.Created,
                            0,
                            false,
                            new List<InvocationNodeDto>(),
                            null))
                        .OrderBy(n => n.Created)
                        .ToList();

                    var turn = turnsByIndex.GetValueOrDefault(turnIndex);
                    var totalCharge = directTools.Sum(x => x.Event.ChargeUsd ?? 0m);

                    results.Add(new TurnInvocationTreeDto(
                        conversationId,
                        turnIndex,
                        turn?.Created ?? DateTime.UtcNow,
                        turn != null ? (long)((turn.LastUpdated - turn.Created).TotalMilliseconds) : 0,
                        totalCharge,
                        0,
                        rootNodes));
                }
                continue;
            }

            // Build tree for this turn
            var rootInvocations = turnInvocations.Where(i => i.ParentInvocationId == null).ToList();
            var rootNodes2 = new List<InvocationNodeDto>();

            foreach (var root in rootInvocations)
            {
                rootNodes2.Add(BuildInvocationTree(
                    root,
                    turnInvocations,
                    toolUsageByInvocation,
                    serviceUsageByInvocation,
                    tokenUsageByInvocation,
                    messagesByInvocation,
                    invocationCosts));
            }

            // Add direct tool events for this turn
            if (directToolEventsByTurn.TryGetValue(turnIndex, out var turnDirectTools))
            {
                foreach (var item in turnDirectTools)
                {
                    // Suppress ReadWeb bridge tool rows - these do not represent billable usage
                    // and would confuse the usage guideants. The underlying crawl usage is
                    // represented separately.
                    if (string.Equals(item.Event.Operation, "ReadWeb", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    rootNodes2.Add(new InvocationNodeDto(
                        item.Event.Id,
                        null,
                        null,
                        $"Tool: {item.Event.Operation}",
                        null,
                        null,
                        0,
                        "completed",
                        0, 0, 0, 0,
                        item.Event.ChargeUsd ?? 0m,
                        item.Event.Created,
                        item.Event.Created,
                        0,
                        false,
                        new List<InvocationNodeDto>(),
                        null));
                }
            }

            rootNodes2 = rootNodes2.OrderBy(n => n.Created).ToList();

            var turnStarted = turnInvocations.Min(i => i.Created);
            var totalDuration = turnInvocations.Max(i => i.DurationMs ?? 0);
            
            var turnInvocationIds = turnInvocations.Select(i => i.Id).ToHashSet();
            var turnTokens = tokenUsageByInvocation
                .Where(kvp => turnInvocationIds.Contains(kvp.Key))
                .Sum(kvp => kvp.Value.PromptTokens + kvp.Value.CompletionTokens);
            var turnInvocationCharges = invocationCosts
                .Where(kvp => turnInvocationIds.Contains(kvp.Key))
                .Sum(kvp => kvp.Value);
            var turnDirectCharges = turnDirectTools?.Sum(x => x.Event.ChargeUsd ?? 0m) ?? 0m;

            results.Add(new TurnInvocationTreeDto(
                conversationId,
                turnIndex,
                turnStarted,
                totalDuration,
                turnInvocationCharges + turnDirectCharges,
                turnTokens,
                rootNodes2));
        }

        return results;
    }

    private async Task<InvocationNodeDto> BuildInvocationNodeAsync(
        AgentInvocation invocation,
        bool includeMessages,
        List<InvocationMessageDto>? messages = null)
    {
        var (promptTokens, completionTokens) = ParseUsage(invocation.UsageJson);
        
        // Calculate charge (approximation based on conversation)
        var chargeUsd = await _context.UsageEvents
            .Where(e => e.ConversationId == invocation.ParentConversationId)
            .SumAsync(e => e.ChargeUsd ?? 0m);

        var invocationsInConversation = await _context.AgentInvocations
            .CountAsync(i => i.ParentConversationId == invocation.ParentConversationId);

        var perInvocationCharge = invocationsInConversation > 0 
            ? chargeUsd / invocationsInConversation 
            : 0m;

        // Build child nodes
        var children = new List<InvocationNodeDto>();
        foreach (var child in invocation.ChildInvocations)
        {
            var childNode = await BuildInvocationNodeAsync(child, false);
            children.Add(childNode);
        }

        return new InvocationNodeDto(
            invocation.Id,
            invocation.ParentInvocationId,
            invocation.TriggeringToolCallId,
            invocation.AssistantName,
            invocation.AssistantId,
            invocation.ModelDeploymentId,
            invocation.Depth,
            invocation.Status,
            promptTokens,
            completionTokens,
            invocation.ToolCallCount,
            invocation.LlmRoundTrips,
            perInvocationCharge,
            invocation.Created,
            invocation.Completed,
            invocation.DurationMs ?? 0,
            false, // IsOutlier - calculated at aggregate level
            children,
            messages
        );
    }

    // Backward-compatible overload used by existing callers that do not need messages or per-invocation costs.
    // Delegates to the full implementation with empty dictionaries.
    private Task<InvocationNodeDto> BuildInvocationTreeAsync(
        AgentInvocation invocation,
        List<AgentInvocation> allInvocations,
        IReadOnlyDictionary<Guid, List<UsageEvent>> toolUsageByInvocation,
        IReadOnlyDictionary<Guid, InvocationTokenAggregate> tokenUsageByInvocation)
    {
        var emptyMessages = (IReadOnlyDictionary<Guid, List<InvocationMessageDto>>)new Dictionary<Guid, List<InvocationMessageDto>>();
        var emptyCosts = (IReadOnlyDictionary<Guid, decimal>)new Dictionary<Guid, decimal>();
        var emptyServices = (IReadOnlyDictionary<Guid, List<UsageEvent>>)new Dictionary<Guid, List<UsageEvent>>();
        return BuildInvocationTreeAsync(invocation, allInvocations, toolUsageByInvocation, emptyServices, tokenUsageByInvocation, emptyMessages, emptyCosts);
    }

    private async Task<InvocationNodeDto> BuildInvocationTreeAsync(
        AgentInvocation invocation,
        List<AgentInvocation> allInvocations,
        IReadOnlyDictionary<Guid, List<UsageEvent>> toolUsageByInvocation,
        IReadOnlyDictionary<Guid, List<UsageEvent>> serviceUsageByInvocation,
        IReadOnlyDictionary<Guid, InvocationTokenAggregate> tokenUsageByInvocation,
        IReadOnlyDictionary<Guid, List<InvocationMessageDto>> messagesByInvocation,
        IReadOnlyDictionary<Guid, decimal> invocationCosts)
    {
        long promptTokens = 0;
        long completionTokens = 0;
        if (tokenUsageByInvocation.TryGetValue(invocation.Id, out var agg))
        {
            promptTokens = agg.PromptTokens;
            completionTokens = agg.CompletionTokens;
        }

        // Aggregate cost for this invocation from usage events (all categories).
        var invocationCost = invocationCosts.GetValueOrDefault(invocation.Id, 0m);
        
        // Find children
        var childInvocations = allInvocations
            .Where(i => i.ParentInvocationId == invocation.Id
                        && !ExcludedAssistantNames.Contains(i.AssistantName))
            .ToList();

        var children = new List<InvocationNodeDto>();
        foreach (var child in childInvocations)
        {
            var childNode = await BuildInvocationTreeAsync(
                child,
                allInvocations,
                toolUsageByInvocation,
                serviceUsageByInvocation,
                tokenUsageByInvocation,
                messagesByInvocation,
                invocationCosts);
            children.Add(childNode);
        }

        // Add tool call usage nodes beneath this invocation for readability
        if (toolUsageByInvocation.TryGetValue(invocation.Id, out var toolEvents))
        {
            foreach (var evt in toolEvents.OrderBy(e => e.Created))
            {
                // Suppress ReadWeb bridge tool rows - these do not represent billable usage
                // and would confuse the usage guideants. The underlying crawl usage is
                // represented separately.
                if (string.Equals(evt.Operation, "ReadWeb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var label = $"Tool: {evt.Operation}";

                children.Add(new InvocationNodeDto(
                    evt.Id,
                    invocation.Id,
                    null,
                    label,
                    invocation.AssistantId,
                    evt.ModelDeploymentId,
                    invocation.Depth + 1,
                    "completed",
                    0,
                    0,
                    0,
                    0,
                    evt.ChargeUsd ?? 0m,
                    evt.Created,
                    evt.Created,
                    0,
                    false,
                    new List<InvocationNodeDto>()));
            }
        }

        // Add AI service usage nodes (all non-chat, non-tool categories) beneath this invocation.
        // We display the UsageEvent.Operation (e.g., "image-generation") as the label so that
        // the tree aligns with Service/Operation/ChargeUsd rows.
        if (serviceUsageByInvocation.TryGetValue(invocation.Id, out var serviceEvents))
        {
            foreach (var evt in serviceEvents.OrderBy(e => e.Created))
            {
                var label = string.IsNullOrWhiteSpace(evt.Operation) ? "AI Service" : evt.Operation;

                children.Add(new InvocationNodeDto(
                    evt.Id,
                    invocation.Id,
                    null,
                    label,
                    null, // AssistantId null so UI can distinguish AI Service from Agent
                    evt.ModelDeploymentId,
                    invocation.Depth + 1,
                    "completed",
                    0,
                    0,
                    0,
                    0,
                    evt.ChargeUsd ?? 0m,
                    evt.Created,
                    evt.Created,
                    0,
                    false,
                    new List<InvocationNodeDto>()));
            }
        }

        messagesByInvocation.TryGetValue(invocation.Id, out var messagesForInvocation);

        return new InvocationNodeDto(
            invocation.Id,
            invocation.ParentInvocationId,
            invocation.TriggeringToolCallId,
            invocation.AssistantName,
            invocation.AssistantId,
            invocation.ModelDeploymentId,
            invocation.Depth,
            invocation.Status,
            promptTokens,
            completionTokens,
            invocation.ToolCallCount,
            invocation.LlmRoundTrips,
            invocationCost,
            invocation.Created,
            invocation.Completed,
            invocation.DurationMs ?? 0,
            false,
            children,
            messagesForInvocation
        );
    }

    private static (long promptTokens, long completionTokens) ParseUsage(string? usageJson)
    {
        if (string.IsNullOrEmpty(usageJson))
            return (0, 0);

        try
        {
            using var doc = JsonDocument.Parse(usageJson);
            var root = doc.RootElement;
            long promptTokens = 0;
            long completionTokens = 0;
            
            if (root.TryGetProperty("promptTokens", out var pt))
                promptTokens = pt.GetInt64();
            if (root.TryGetProperty("completionTokens", out var ct))
                completionTokens = ct.GetInt64();
                
            return (promptTokens, completionTokens);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static long ParseTokens(string? usageJson)
    {
        var (prompt, completion) = ParseUsage(usageJson);
        return prompt + completion;
    }

    private static double CalculateStdDev(List<double> values, double mean)
    {
        if (values.Count == 0)
            return 0;
        
        var sumOfSquares = values.Sum(v => Math.Pow(v - mean, 2));
        return Math.Sqrt(sumOfSquares / values.Count);
    }

    /// <inheritdoc />
    public async Task<TurnMessagesDto?> GetTurnMessagesAsync(
        Guid conversationId,
        int turnIndex)
    {
        // Get the conversation with its turn
        var conversation = await _context.NotebookConversations
            .Include(c => c.Notebook)
                .ThenInclude(n => n.Project)
            .Include(c => c.Turns)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null) return null;

        var turn = conversation.Turns.FirstOrDefault(t => t.TurnIndex == turnIndex);
        if (turn == null) return null;

        // Get messages for this turn
        var messages = await _context.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.TurnIndex == turnIndex)
            .OrderBy(m => m.MessageSequence)
            .ToListAsync();

        // Get AgentInvocations triggered by tool calls in this turn
        var toolCallIds = messages
            .Where(m => m.ToolCallId != null)
            .Select(m => m.ToolCallId!)
            .Distinct()
            .ToList();

        var invocationsByToolCallId = await _context.AgentInvocations
            .Where(ai => ai.ParentConversationId == conversationId
                      && ai.TriggeringToolCallId != null
                      && toolCallIds.Contains(ai.TriggeringToolCallId))
            .ToDictionaryAsync(ai => ai.TriggeringToolCallId!, ai => ai.Id);

        // Build message DTOs with invocation links
        var messageDtos = messages.Select(m => new TurnMessageDto(
            m.Id,
            m.Role.ToString(),
            m.Content,
            m.ToolCalls,
            m.ToolCallId,
            m.FunctionName,
            m.Created,
            m.ToolCallId != null && invocationsByToolCallId.TryGetValue(m.ToolCallId, out var invId) ? invId : null
        )).ToList();

        return new TurnMessagesDto(
            conversationId,
            turnIndex,
            turn.AssistantName,
            turn.Created,
            messageDtos
        );
    }

    private static string NormalizeSourceFilter(string? sourceFilter)
    {
        if (string.IsNullOrWhiteSpace(sourceFilter))
        {
            return "all";
        }

        return sourceFilter.Trim().ToLowerInvariant() switch
        {
            "conversation" => "conversation",
            "published_chat" => "published_chat",
            "mcp" => "mcp",
            "wire_api" => "wire_api",
            _ => "all"
        };
    }

    private static IQueryable<UsageEvent> ApplySourceFilter(IQueryable<UsageEvent> query, string sourceFilter)
    {
        return sourceFilter switch
        {
            "conversation" => query.Where(e =>
                string.IsNullOrEmpty(e.SourceChannel) ||
                e.SourceChannel == "conversation"),
            "published_chat" => query.Where(e => e.SourceChannel == "published_chat"),
            "mcp" => query.Where(e => e.SourceChannel == "mcp"),
            "wire_api" => query.Where(e => e.SourceChannel == "wire_api"),
            _ => query
        };
    }

    private static (string SourceChannel, string Endpoint, string Alias, string ProviderServiceMode, string StatusFamily, decimal ChargeUsd) MapApiUsageSlice(ApiUsageSlice slice)
    {
        var sourceChannel = string.IsNullOrWhiteSpace(slice.SourceChannel)
            ? "conversation"
            : slice.SourceChannel.Trim().ToLowerInvariant();
        var endpoint = string.IsNullOrWhiteSpace(slice.Operation)
            ? "unknown"
            : slice.Operation.Trim().ToLowerInvariant();
        var alias = "n/a";
        var providerServiceMode = "n/a";
        var statusFamily = "unknown";

        if (!string.IsNullOrWhiteSpace(slice.MetadataJson))
        {
            try
            {
                using var metadata = JsonDocument.Parse(slice.MetadataJson);
                var root = metadata.RootElement;

                if (TryGetString(root, "endpoint", out var endpointValue))
                {
                    endpoint = endpointValue!.Trim().ToLowerInvariant();
                }

                if (TryGetString(root, "alias", out var aliasValue) && !string.IsNullOrWhiteSpace(aliasValue))
                {
                    alias = aliasValue!.Trim();
                }

                if (TryGetString(root, "providerServiceMode", out var modeValue) && !string.IsNullOrWhiteSpace(modeValue))
                {
                    providerServiceMode = modeValue!.Trim();
                }

                if (TryGetString(root, "status", out var statusValue))
                {
                    statusFamily = ResolveStatusFamily(statusValue);
                }
            }
            catch (JsonException)
            {
                // Best effort metadata parsing for mixed historic payloads.
            }
        }

        return (sourceChannel, endpoint, alias, providerServiceMode, statusFamily, slice.ChargeUsd);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static string ResolveStatusFamily(string? rawStatus)
    {
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return "unknown";
        }

        var status = rawStatus.Trim().ToLowerInvariant();
        if (status.StartsWith("2", StringComparison.Ordinal))
        {
            return "success";
        }

        if (status.StartsWith("4", StringComparison.Ordinal))
        {
            return "client_error";
        }

        if (status.StartsWith("5", StringComparison.Ordinal))
        {
            return "server_error";
        }

        if (status is "success" or "ok" or "completed")
        {
            return "success";
        }

        if (status.Contains("error", StringComparison.Ordinal) ||
            status.Contains("fail", StringComparison.Ordinal) ||
            status.Contains("denied", StringComparison.Ordinal))
        {
            return "server_error";
        }

        return status;
    }
}

