import { useEffect, useMemo, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { guideUsageApi } from '../services/guideUsageApi';
import { api } from '../services/api';
import type {
  GuideUsageReportDto,
  GuideUsageSummaryDto,
  DailyUsageBucketDto,
  ConversationUsageSummaryDto,
  GuideApiUsageReportDto,
  GuideUsageSourceFilter,
} from '../types/usage';
import { ConversationDetailPanel } from '../components/usage/ConversationDetailPanel';
import type { GuideDto, AssistantDto } from '../types/guides';
import { getApiOrigin } from '../config/apiConfig';
import {
  AreaChart,
  Area,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
} from 'recharts';

type RangePreset = '7d' | '30d' | '90d';

export default function GuideUsagePage() {
  const { projectId, guideId, assistantId } = useParams<{ projectId: string; guideId?: string; assistantId?: string }>();
  const navigate = useNavigate();
  
  // Support both guide and assistant routes
  const entityId = guideId || assistantId;
  const isAssistant = !!assistantId;
  
  const [range, setRange] = useState<RangePreset>('7d');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<GuideUsageReportDto | null>(null);
  const [loadingCharts, setLoadingCharts] = useState(false);
  const [loadingCrew, setLoadingCrew] = useState(false);
  const [loadingConversations, setLoadingConversations] = useState(false);
  const [loadingApiUsage, setLoadingApiUsage] = useState(false);
  const [chartsError, setChartsError] = useState<string | null>(null);
  const [crewError, setCrewError] = useState<string | null>(null);
  const [conversationsError, setConversationsError] = useState<string | null>(null);
  const [apiUsageError, setApiUsageError] = useState<string | null>(null);
  const [apiUsageSource, setApiUsageSource] = useState<GuideUsageSourceFilter>('all');
  const [apiUsageReport, setApiUsageReport] = useState<GuideApiUsageReportDto | null>(null);
  const [conversationPage, setConversationPage] = useState(1);
  const [conversationPageSize] = useState(100);
  const [conversationTotalCount, setConversationTotalCount] = useState(0);
  const [entity, setEntity] = useState<GuideDto | AssistantDto | null>(null);
  const [avatarUrl, setAvatarUrl] = useState<string | null>(null);
  const [chartTab, setChartTab] = useState<'tokens' | 'cost'>('tokens');
  const [tokenMetric, setTokenMetric] = useState<'direct' | 'total'>('direct');
  const [costMetric, setCostMetric] = useState<'direct' | 'total'>('total');
  const [selectedConversation, setSelectedConversation] =
    useState<ConversationUsageSummaryDto | null>(null);

  const allowedAssistantIds = useMemo<string[]>(() => {
    if (!entity) return [];
    const ids = [entity.id];
    if (!isAssistant && report?.crewMembers) {
      for (const cm of report.crewMembers) {
        ids.push(cm.assistantId);
      }
    }
    return ids;
  }, [entity, isAssistant, report?.crewMembers]);

  // Calculate date range
  const [from, to] = useMemo((): [string, string] => {
    const now = new Date();
    const startUtcMidnight = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
    if (range === '7d') startUtcMidnight.setUTCDate(startUtcMidnight.getUTCDate() - 7);
    else if (range === '30d') startUtcMidnight.setUTCDate(startUtcMidnight.getUTCDate() - 30);
    else if (range === '90d') startUtcMidnight.setUTCDate(startUtcMidnight.getUTCDate() - 90);
    return [startUtcMidnight.toISOString(), now.toISOString()];
  }, [range]);

  // Load guide/assistant details
  useEffect(() => {
    if (!entityId) return;
    
    let cancelled = false;
    let loadedAvatarUrl: string | null = null;
    
    async function loadEntity() {
      try {
        let entityData: GuideDto | AssistantDto | null = null;
        let entityAvatarUrl: string | undefined;
        
        if (isAssistant) {
          const assistantDetails = await api.guides.assistants.get(entityId!);
          if (!cancelled && assistantDetails) {
            entityData = assistantDetails.assistant;
            entityAvatarUrl = assistantDetails.assistant.avatarUrl;
          }
        } else {
          const guideDetails = await api.guides.guides.get(entityId!);
          if (!cancelled && guideDetails) {
            entityData = guideDetails.guide;
            entityAvatarUrl = guideDetails.guide.avatarUrl;
          }
        }
        
        if (!cancelled && entityData) {
          setEntity(entityData);
          
          // Load avatar if available
          if (entityAvatarUrl) {
            try {
              const apiOrigin = getApiOrigin();
              const fullUrl = `${apiOrigin}${entityAvatarUrl}`;
              const result = await api.utils.getAuthenticatedUrl(fullUrl);
              if (!cancelled) {
                loadedAvatarUrl = result.objectUrl;
                setAvatarUrl(result.objectUrl);
              }
            } catch {
              // Ignore avatar load errors
            }
          }
        }
      } catch (e) {
        console.error('Failed to load entity:', e);
      }
    }
    
    loadEntity();
    return () => { 
      cancelled = true;
      if (loadedAvatarUrl && loadedAvatarUrl.startsWith('blob:')) {
        URL.revokeObjectURL(loadedAvatarUrl);
      }
    };
  }, [entityId, isAssistant]);

  // Load usage report
  useEffect(() => {
    if (!projectId || !entityId) return;
    
    let cancelled = false;
    
    async function loadReport() {
      setLoading(true);
      setError(null);
      setChartsError(null);
      setCrewError(null);
      setConversationsError(null);
      setApiUsageError(null);
      setApiUsageReport(null);
      setConversationPage(1);
      setConversationTotalCount(0);
      try {
        const summary = isAssistant
          ? await guideUsageApi.getAssistantUsageSummary(projectId!, entityId!, from, to)
          : await guideUsageApi.getGuideUsageSummary(projectId!, entityId!, from, to);

        if (!cancelled) {
          setReport(mapSummaryToReport(summary));
          setLoading(false);
        }

        if (!cancelled) {
          setLoadingCharts(true);
          try {
            const buckets = isAssistant
              ? await guideUsageApi.getAssistantUsageCharts(projectId!, entityId!, from, to)
              : await guideUsageApi.getGuideUsageCharts(projectId!, entityId!, from, to);
            if (!cancelled) {
              setReport(prev => prev ? { ...prev, dailyBuckets: buckets } : prev);
            }
          } catch (e: unknown) {
            if (!cancelled) {
              setChartsError(e instanceof Error ? e.message : 'Failed to load chart data');
            }
          } finally {
            if (!cancelled) setLoadingCharts(false);
          }
        }

        if (!cancelled) {
          setLoadingCrew(true);
          try {
            const crew = isAssistant
              ? await guideUsageApi.getAssistantUsageCrew(projectId!, entityId!, from, to)
              : await guideUsageApi.getGuideUsageCrew(projectId!, entityId!, from, to);
            if (!cancelled) {
              setReport(prev => prev ? { ...prev, crewMembers: crew.crewMembers, directToolCalls: crew.directToolCalls } : prev);
            }
          } catch (e: unknown) {
            if (!cancelled) {
              setCrewError(e instanceof Error ? e.message : 'Failed to load crew/tool usage');
            }
          } finally {
            if (!cancelled) setLoadingCrew(false);
          }
        }

        if (!cancelled) {
          setLoadingConversations(true);
          try {
            const conversations = isAssistant
              ? await guideUsageApi.getAssistantUsageConversations(projectId!, entityId!, from, to, 1, conversationPageSize)
              : await guideUsageApi.getGuideUsageConversations(projectId!, entityId!, from, to, 1, conversationPageSize);
            if (!cancelled) {
              setReport(prev => prev ? { ...prev, conversations: conversations.items } : prev);
              setConversationTotalCount(conversations.totalCount);
            }
          } catch (e: unknown) {
            if (!cancelled) {
              setConversationsError(e instanceof Error ? e.message : 'Failed to load conversations');
            }
          } finally {
            if (!cancelled) setLoadingConversations(false);
          }
        }
      } catch (e: unknown) {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : 'Failed to load usage report');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }
    
    loadReport();
    return () => { cancelled = true; };
  }, [projectId, entityId, isAssistant, from, to]);

  useEffect(() => {
    if (!projectId || !entityId) return;
    const currentProjectId = projectId;
    const currentEntityId = entityId;

    let cancelled = false;

    async function loadApiUsage() {
      setLoadingApiUsage(true);
      setApiUsageError(null);
      try {
        const next = isAssistant
          ? await guideUsageApi.getAssistantApiUsage(currentProjectId, currentEntityId, from, to, apiUsageSource)
          : await guideUsageApi.getGuideApiUsage(currentProjectId, currentEntityId, from, to, apiUsageSource);
        if (!cancelled) {
          setApiUsageReport(next);
        }
      } catch (e: unknown) {
        if (!cancelled) {
          setApiUsageError(e instanceof Error ? e.message : 'Failed to load API usage');
        }
      } finally {
        if (!cancelled) {
          setLoadingApiUsage(false);
        }
      }
    }

    loadApiUsage();
    return () => {
      cancelled = true;
    };
  }, [projectId, entityId, isAssistant, from, to, apiUsageSource]);

  function mapSummaryToReport(summary: GuideUsageSummaryDto): GuideUsageReportDto {
    return {
      ...summary,
      crewMembers: [],
      directToolCalls: [],
      dailyBuckets: [],
      conversations: [],
    };
  }

  const loadMoreConversations = async () => {
    if (!projectId || !entityId || !report || loadingConversations) return;
    const nextPage = conversationPage + 1;
    setLoadingConversations(true);
    setConversationsError(null);
    try {
      const response = isAssistant
        ? await guideUsageApi.getAssistantUsageConversations(projectId, entityId, from, to, nextPage, conversationPageSize)
        : await guideUsageApi.getGuideUsageConversations(projectId, entityId, from, to, nextPage, conversationPageSize);
      setReport(prev => prev ? { ...prev, conversations: [...prev.conversations, ...response.items] } : prev);
      setConversationPage(nextPage);
      setConversationTotalCount(response.totalCount);
    } catch (e: unknown) {
      setConversationsError(e instanceof Error ? e.message : 'Failed to load more conversations');
    } finally {
      setLoadingConversations(false);
    }
  };

  const handleBack = () => {
    const tab = isAssistant ? 'assistants' : 'guides';
    navigate(`/projects/${projectId}/guides?tab=${tab}`);
  };

  const formatNumber = (value: number, decimals: number = 2) => {
    if (Number.isInteger(value)) return value.toString();
    return value.toFixed(decimals);
  };

  const formatTokens = (value: number) =>
    new Intl.NumberFormat('en-US').format(value);

  const formatCurrency = (value: number) =>
    new Intl.NumberFormat('en-US', { 
      style: 'currency', 
      currency: 'USD', 
      minimumFractionDigits: 2,
      maximumFractionDigits: 4
    }).format(value);

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr);
    return new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric' }).format(date);
  };

  const formatSourceLabel = (value: string) => {
    switch (value) {
      case 'conversation':
        return 'Conversation';
      case 'published_chat':
        return 'Published Chat';
      case 'mcp':
        return 'MCP';
      case 'wire_api':
        return 'Wire API';
      default:
        return value;
    }
  };

  // Prepare chart data
  const chartData = useMemo(() => {
    if (!report?.dailyBuckets) return [];
    return report.dailyBuckets.map((bucket: DailyUsageBucketDto) => ({
      date: formatDate(bucket.date),
      directPromptTokens: bucket.promptTokens,
      directCachedTokens: bucket.cachedTokens,
      directReasoningTokens: bucket.reasoningTokens,
      directCompletionTokens: bucket.completionTokens,
      totalPromptTokens: bucket.promptTokensWithCrew,
      totalCachedTokens: bucket.cachedTokensWithCrew,
      totalReasoningTokens: bucket.reasoningTokensWithCrew,
      totalCompletionTokens: bucket.completionTokensWithCrew,
      directCost: bucket.directCost,
      totalCost: bucket.totalCost,
    }));
  }, [report?.dailyBuckets]);

  // Debug logging to understand why token charts might not render as expected
  useEffect(() => {
    if (!report) return;

    // Log raw daily buckets from API
    // eslint-disable-next-line no-console
    console.log('[GuideUsagePage] raw dailyBuckets', report.dailyBuckets);
  }, [report]);

  useEffect(() => {
    if (!chartData.length) {
      // eslint-disable-next-line no-console
      console.log('[GuideUsagePage] chartData is empty', { tokenMetric, chartTab });
      return;
    }

    const metricKeys =
      tokenMetric === 'total'
        ? ['totalPromptTokens', 'totalCachedTokens', 'totalReasoningTokens', 'totalCompletionTokens']
        : ['directPromptTokens', 'directCachedTokens', 'directReasoningTokens', 'directCompletionTokens'];

    const values: number[] = [];
    for (const point of chartData as any[]) {
      for (const key of metricKeys) {
        const v = point[key];
        if (typeof v === 'number') {
          values.push(v);
        }
      }
    }

    const max = values.length ? Math.max(...values) : 0;
    const nonZero = values.filter(v => v > 0);
    const minNonZero = nonZero.length ? Math.min(...nonZero) : 0;

    // eslint-disable-next-line no-console
    console.log('[GuideUsagePage] token series debug', {
      tokenMetric,
      metricKeys,
      minNonZero,
      max,
      points: chartData.length,
    });
  }, [chartData, tokenMetric, chartTab]);

  // Check if we have any reasoning tokens
  const hasReasoningTokens = report?.totalReasoningTokens && report.totalReasoningTokens > 0;

  const directTotalTokens =
    (report?.totalPromptTokens ?? 0) +
    (report?.totalCachedTokens ?? 0) +
    (report?.totalReasoningTokens ?? 0) +
    (report?.totalCompletionTokens ?? 0);

  const totalTokensWithCrew =
    (report?.totalPromptTokensWithCrew ?? 0) +
    (report?.totalCachedTokensWithCrew ?? 0) +
    (report?.totalReasoningTokensWithCrew ?? 0) +
    (report?.totalCompletionTokensWithCrew ?? 0);

  return (
    <div className="h-full overflow-auto bg-gray-50 p-4 md:p-8">
      <div className="max-w-7xl mx-auto">
        {/* Header */}
        <div className="flex items-center mb-6 md:mb-8">
          <button 
            onClick={handleBack}
            className="mr-3 p-1.5 text-gray-500 hover:bg-gray-100 rounded-md"
            aria-label="Back"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
            </svg>
          </button>
          
          <div className="flex-shrink-0 mr-4">
            {avatarUrl ? (
              <img
                src={avatarUrl}
                alt={entity?.name || (isAssistant ? 'Assistant' : 'Guide')}
                className="w-12 h-12 rounded-lg object-cover"
              />
            ) : (
              <div className={`w-12 h-12 rounded-lg flex items-center justify-center ${
                isAssistant 
                  ? 'bg-gradient-to-br from-green-500 to-teal-600' 
                  : 'bg-gradient-to-br from-blue-500 to-purple-600'
              }`}>
                <svg className="w-6 h-6 text-white" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                </svg>
              </div>
            )}
          </div>
          
          <div className="min-w-0 flex-1">
            <h1 className="text-lg md:text-xl font-semibold truncate">
              {report?.guideName || entity?.name || (isAssistant ? 'Assistant' : 'Guide')} Usage Report
            </h1>
            <p className="text-xs md:text-sm text-gray-600">
              {report ? `${report.totalConversations} conversations in selected period` : 'Loading...'}
            </p>
          </div>
        </div>

        {/* Date Range Selector */}
        <div className="flex items-center mb-6">
          <select
            className="border border-gray-300 rounded px-3 py-1.5 text-sm bg-white"
            value={range}
            onChange={e => setRange(e.target.value as RangePreset)}
          >
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
            <option value="90d">Last 90 days</option>
          </select>
        </div>

        {/* Loading State */}
        {loading && (
          <div className="flex items-center justify-center py-12">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"></div>
            <span className="ml-3 text-gray-600">Loading usage data...</span>
          </div>
        )}

        {/* Error State */}
        {error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-6">
            <div className="text-red-700">{error}</div>
          </div>
        )}

        {/* Content */}
        {report && (
          <div className="space-y-6">
            {/* Summary Cards */}
            <div className="grid grid-cols-2 md:grid-cols-6 gap-4">
              <button
                type="button"
                onClick={() => {
                  setChartTab('tokens');
                  setTokenMetric('direct');
                }}
                className={`text-left bg-white rounded-lg border p-4 transition-colors ${
                  chartTab === 'tokens' && tokenMetric === 'direct'
                    ? 'border-blue-500 shadow-sm'
                    : 'border-gray-200 hover:border-gray-300'
                }`}
              >
                <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">Direct Tokens</div>
                <div className="text-xl font-semibold text-gray-900">
                  {formatTokens(directTotalTokens)}
                </div>
                <div className="text-xs text-gray-500 mt-1">
                  {formatTokens(report.totalPromptTokens)} in
                  {report.totalCachedTokens > 0 && ` / ${formatTokens(report.totalCachedTokens)} cached`}
                  {report.totalReasoningTokens > 0 && ` / ${formatTokens(report.totalReasoningTokens)} reasoning`}
                  {' / '}{formatTokens(report.totalCompletionTokens)} out
                </div>
              </button>
              <button
                type="button"
                onClick={() => {
                  setChartTab('tokens');
                  setTokenMetric('total');
                }}
                className={`text-left bg-white rounded-lg border p-4 transition-colors ${
                  chartTab === 'tokens' && tokenMetric === 'total'
                    ? 'border-blue-500 shadow-sm'
                    : 'border-gray-200 hover:border-gray-300'
                }`}
              >
                <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">Total Tokens</div>
                <div className="text-xl font-semibold text-gray-900">
                  {formatTokens(totalTokensWithCrew)}
                </div>
                <div className="text-xs text-gray-500 mt-1">
                  Direct + Crew
                </div>
              </button>
              <button
                type="button"
                onClick={() => {
                  setChartTab('cost');
                  setCostMetric('direct');
                }}
                className={`text-left bg-white rounded-lg border p-4 transition-colors ${
                  chartTab === 'cost' && costMetric === 'direct'
                    ? 'border-blue-500 shadow-sm'
                    : 'border-gray-200 hover:border-gray-300'
                }`}
              >
                <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">Direct Cost</div>
                <div className="text-xl font-semibold text-gray-900">
                  {formatCurrency(report.directCost)}
                </div>
              </button>
              <button
                type="button"
                onClick={() => {
                  setChartTab('cost');
                  setCostMetric('total');
                }}
                className={`text-left bg-white rounded-lg border p-4 transition-colors ${
                  chartTab === 'cost' && costMetric === 'total'
                    ? 'border-blue-500 shadow-sm'
                    : 'border-gray-200 hover:border-gray-300'
                }`}
              >
                <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">Total Cost</div>
                <div className="text-xl font-semibold text-gray-900">
                  {formatCurrency(report.totalCost)}
                </div>
                <div className="text-xs text-gray-500 mt-1">
                  Direct + Crew
                </div>
              </button>
              <div className="bg-white rounded-lg border border-gray-200 p-4">
                <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">Conversations</div>
                <div className="text-xl font-semibold text-gray-900">
                  {report.totalConversations}
                </div>
              </div>
              <div className="bg-white rounded-lg border border-gray-200 p-4">
                <div className="text-xs text-gray-500 uppercase tracking-wider mb-1">Avg Cost / Conv</div>
                <div className="text-xl font-semibold text-gray-900">
                  {report.totalConversations > 0 
                    ? formatCurrency(report.totalCost / report.totalConversations)
                    : formatCurrency(0)}
                </div>
              </div>
            </div>

            {/* Charts - Tabbed */}
            <div className="bg-white rounded-lg border border-gray-200 overflow-hidden">
              <div className="px-4 py-3 border-b border-gray-200 bg-gray-50 flex items-center gap-4">
                <button
                  onClick={() => setChartTab('tokens')}
                  className={`text-sm font-semibold px-3 py-1.5 rounded-md transition-colors ${
                    chartTab === 'tokens'
                      ? 'bg-white text-gray-900 shadow-sm'
                      : 'text-gray-500 hover:text-gray-700'
                  }`}
                >
                  Token Usage
                </button>
                <button
                  onClick={() => setChartTab('cost')}
                  className={`text-sm font-semibold px-3 py-1.5 rounded-md transition-colors ${
                    chartTab === 'cost'
                      ? 'bg-white text-gray-900 shadow-sm'
                      : 'text-gray-500 hover:text-gray-700'
                  }`}
                >
                  Cost
                </button>
              </div>

              {chartsError && (
                <div className="px-4 py-3 text-sm text-red-600 border-b border-red-100 bg-red-50">
                  {chartsError}
                </div>
              )}

              {chartTab === 'tokens' && (
                <>
                  <div className="p-4">
                    {loadingCharts ? (
                      <div className="h-[250px] flex items-center justify-center text-gray-500 text-sm">
                        Loading chart data...
                      </div>
                    ) : chartData.length > 0 ? (
                      <ResponsiveContainer width="100%" height={250}>
                        <AreaChart data={chartData} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
                          <defs>
                            <linearGradient id="colorPrompt" x1="0" y1="0" x2="0" y2="1">
                              <stop offset="5%" stopColor="#3b82f6" stopOpacity={0.3}/>
                              <stop offset="95%" stopColor="#3b82f6" stopOpacity={0}/>
                            </linearGradient>
                            <linearGradient id="colorCached" x1="0" y1="0" x2="0" y2="1">
                              <stop offset="5%" stopColor="#8b5cf6" stopOpacity={0.3}/>
                              <stop offset="95%" stopColor="#8b5cf6" stopOpacity={0}/>
                            </linearGradient>
                            <linearGradient id="colorReasoning" x1="0" y1="0" x2="0" y2="1">
                              <stop offset="5%" stopColor="#f59e0b" stopOpacity={0.3}/>
                              <stop offset="95%" stopColor="#f59e0b" stopOpacity={0}/>
                            </linearGradient>
                            <linearGradient id="colorCompletion" x1="0" y1="0" x2="0" y2="1">
                              <stop offset="5%" stopColor="#10b981" stopOpacity={0.3}/>
                              <stop offset="95%" stopColor="#10b981" stopOpacity={0}/>
                            </linearGradient>
                          </defs>
                          <CartesianGrid strokeDasharray="3 3" stroke="#e5e7eb" />
                          <XAxis 
                            dataKey="date" 
                            tick={{ fontSize: 11, fill: '#6b7280' }}
                            tickLine={false}
                            axisLine={{ stroke: '#e5e7eb' }}
                          />
                          <YAxis 
                            tick={{ fontSize: 11, fill: '#6b7280' }}
                            tickLine={false}
                            axisLine={{ stroke: '#e5e7eb' }}
                            tickFormatter={(v) => v >= 1000 ? `${(v / 1000).toFixed(0)}k` : v}
                          />
                          <Tooltip
                            contentStyle={{ 
                              backgroundColor: 'white', 
                              border: '1px solid #e5e7eb',
                              borderRadius: '8px',
                              fontSize: '12px'
                            }}
                            formatter={(value: number, name: string) => [
                              formatTokens(value),
                              name === 'promptTokens' ? 'Input Tokens' : 
                              name === 'cachedTokens' ? 'Cached Tokens' :
                              name === 'reasoningTokens' ? 'Reasoning Tokens' : 'Output Tokens'
                            ]}
                          />
                          <Area
                            type="monotone"
                            dataKey={tokenMetric === 'total' ? 'totalCompletionTokens' : 'directCompletionTokens'}
                            stackId="1"
                            stroke="#10b981"
                            fill="url(#colorCompletion)"
                            name="completionTokens"
                          />
                          {hasReasoningTokens && (
                            <Area
                              type="monotone"
                              dataKey={tokenMetric === 'total' ? 'totalReasoningTokens' : 'directReasoningTokens'}
                              stackId="1"
                              stroke="#f59e0b"
                              fill="url(#colorReasoning)"
                              name="reasoningTokens"
                            />
                          )}
                          <Area
                            type="monotone"
                            dataKey={tokenMetric === 'total' ? 'totalCachedTokens' : 'directCachedTokens'}
                            stackId="1"
                            stroke="#8b5cf6"
                            fill="url(#colorCached)"
                            name="cachedTokens"
                          />
                          <Area
                            type="monotone"
                            dataKey={tokenMetric === 'total' ? 'totalPromptTokens' : 'directPromptTokens'}
                            stackId="1"
                            stroke="#3b82f6"
                            fill="url(#colorPrompt)"
                            name="promptTokens"
                          />
                        </AreaChart>
                      </ResponsiveContainer>
                    ) : (
                      <div className="h-[250px] flex items-center justify-center text-gray-500 text-sm">
                        No token usage data for the selected period
                      </div>
                    )}
                  </div>
                  <div className="px-4 pb-3 flex items-center gap-4 text-xs">
                    <div className="flex items-center gap-1.5">
                      <div className="w-3 h-3 rounded bg-blue-500"></div>
                      <span className="text-gray-600">Input Tokens</span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <div className="w-3 h-3 rounded bg-violet-500"></div>
                      <span className="text-gray-600">Cached Tokens</span>
                    </div>
                    {hasReasoningTokens && (
                      <div className="flex items-center gap-1.5">
                        <div className="w-3 h-3 rounded bg-amber-500"></div>
                        <span className="text-gray-600">Reasoning Tokens</span>
                      </div>
                    )}
                    <div className="flex items-center gap-1.5">
                      <div className="w-3 h-3 rounded bg-emerald-500"></div>
                      <span className="text-gray-600">Output Tokens</span>
                    </div>
                  </div>
                </>
              )}

              {chartTab === 'cost' && (
                <div className="p-4">
                  {loadingCharts ? (
                    <div className="h-[250px] flex items-center justify-center text-gray-500 text-sm">
                      Loading chart data...
                    </div>
                  ) : chartData.length > 0 ? (
                    <ResponsiveContainer width="100%" height={250}>
                      <BarChart data={chartData} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
                        <CartesianGrid strokeDasharray="3 3" stroke="#e5e7eb" />
                        <XAxis 
                          dataKey="date" 
                          tick={{ fontSize: 11, fill: '#6b7280' }}
                          tickLine={false}
                          axisLine={{ stroke: '#e5e7eb' }}
                        />
                        <YAxis 
                          tick={{ fontSize: 11, fill: '#6b7280' }}
                          tickLine={false}
                          axisLine={{ stroke: '#e5e7eb' }}
                          tickFormatter={(v) => `$${v.toFixed(2)}`}
                        />
                        <Tooltip
                          contentStyle={{ 
                            backgroundColor: 'white', 
                            border: '1px solid #e5e7eb',
                            borderRadius: '8px',
                            fontSize: '12px'
                          }}
                          formatter={(value: number) => [
                            formatCurrency(value),
                            costMetric === 'total' ? 'Total Cost' : 'Direct Cost',
                          ]}
                        />
                        <Bar 
                          dataKey={costMetric === 'total' ? 'totalCost' : 'directCost'}
                          fill="#8b5cf6" 
                          radius={[4, 4, 0, 0]}
                        />
                      </BarChart>
                    </ResponsiveContainer>
                  ) : (
                    <div className="h-[250px] flex items-center justify-center text-gray-500 text-sm">
                      No cost data for the selected period
                    </div>
                  )}
                </div>
              )}
            </div>

            {/* Crew Members Table - Only for guides with crew members */}
            {crewError && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-4">
                <div className="text-red-700">{crewError}</div>
              </div>
            )}

            {!isAssistant && report.crewMembers.length > 0 && (
              <div className="bg-white rounded-lg border border-gray-200 overflow-hidden">
                <div className="px-4 py-3 border-b border-gray-200 bg-gray-50">
                  <h2 className="text-sm font-semibold text-gray-900">Crew Members</h2>
                </div>
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Assistant
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Total Invocations
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Avg / Conversation
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Total Cost
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Avg Cost / Conversation
                        </th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {report.crewMembers.map((cm) => (
                        <tr key={cm.assistantId} className="hover:bg-gray-50">
                          <td className="px-4 py-3 whitespace-nowrap text-sm font-medium text-gray-900">
                            {cm.assistantName}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {cm.totalInvocations}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {formatNumber(cm.averageInvocationsPerConversation)}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {formatCurrency(cm.totalCost)}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {formatCurrency(cm.averageCostPerConversation)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            {!isAssistant && loadingCrew && report.crewMembers.length === 0 && (
              <div className="bg-white rounded-lg border border-gray-200 p-6 text-sm text-gray-500">
                Loading crew usage...
              </div>
            )}

            {/* Direct Tool Calls Table */}
            {report.directToolCalls.length > 0 && (
              <div className="bg-white rounded-lg border border-gray-200 overflow-hidden">
                <div className="px-4 py-3 border-b border-gray-200 bg-gray-50">
                  <h2 className="text-sm font-semibold text-gray-900">Direct Tool Calls</h2>
                </div>
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Tool
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Total Calls
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Avg / Conversation
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Total Cost
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Avg Cost / Conversation
                        </th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {report.directToolCalls.map((tool) => (
                        <tr key={tool.toolName} className="hover:bg-gray-50">
                          <td className="px-4 py-3 whitespace-nowrap text-sm font-medium text-gray-900 font-mono">
                            {tool.toolName}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {tool.totalCalls}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {formatNumber(tool.averageCallsPerConversation)}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {formatCurrency(tool.totalCost)}
                          </td>
                          <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                            {formatCurrency(tool.averageCostPerConversation)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}

            <div className="bg-white rounded-lg border border-gray-200 overflow-hidden">
              <div className="px-4 py-3 border-b border-gray-200 bg-gray-50 flex items-center justify-between gap-3">
                <div>
                  <h2 className="text-sm font-semibold text-gray-900">API Usage</h2>
                  <p className="text-xs text-gray-500 mt-0.5">
                    Grouped by source channel, endpoint, alias, provider mode, and status.
                  </p>
                </div>
                <select
                  className="border border-gray-300 rounded px-2 py-1 text-xs bg-white"
                  value={apiUsageSource}
                  onChange={(e) => setApiUsageSource(e.target.value as GuideUsageSourceFilter)}
                >
                  <option value="all">All Sources</option>
                  <option value="conversation">Conversation</option>
                  <option value="published_chat">Published Chat</option>
                  <option value="mcp">MCP</option>
                  <option value="wire_api">Wire API</option>
                </select>
              </div>
              {apiUsageError && (
                <div className="px-4 py-3 text-sm text-red-700 border-b border-red-100 bg-red-50">
                  {apiUsageError}
                </div>
              )}
              {loadingApiUsage ? (
                <div className="p-6 text-sm text-gray-500">Loading API usage...</div>
              ) : apiUsageReport && apiUsageReport.rows.length > 0 ? (
                <>
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200">
                      <thead className="bg-gray-50">
                        <tr>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Source</th>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Endpoint</th>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Alias</th>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Provider Mode</th>
                          <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">Status</th>
                          <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Events</th>
                          <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">Charge</th>
                        </tr>
                      </thead>
                      <tbody className="bg-white divide-y divide-gray-200">
                        {apiUsageReport.rows.map((row, idx) => (
                          <tr key={`${row.sourceChannel}-${row.endpoint}-${row.alias}-${row.providerServiceMode}-${row.statusFamily}-${idx}`} className="hover:bg-gray-50">
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-800">{formatSourceLabel(row.sourceChannel)}</td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-800 font-mono">{row.endpoint}</td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-700 font-mono">{row.alias}</td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-700 font-mono">{row.providerServiceMode}</td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-700">{row.statusFamily}</td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">{row.events}</td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">{formatCurrency(row.chargeUsd)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <div className="px-4 py-2 border-t border-gray-200 bg-gray-50 text-xs text-gray-600 flex items-center justify-end gap-4">
                    <span>Total events: <strong>{apiUsageReport.totalEvents}</strong></span>
                    <span>Total charge: <strong>{formatCurrency(apiUsageReport.totalChargeUsd)}</strong></span>
                  </div>
                </>
              ) : (
                <div className="p-6 text-sm text-gray-500">No API usage found for this filter.</div>
              )}
            </div>

            {/* Conversations Table */}
            {report.conversations.length > 0 && (
              <div className="bg-white rounded-lg border border-gray-200 overflow-hidden">
                <div className="px-4 py-3 border-b border-gray-200 bg-gray-50">
                  <h2 className="text-sm font-semibold text-gray-900">
                    Conversations ({report.conversations.length} / {conversationTotalCount || report.conversations.length})
                  </h2>
                </div>
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Title
                        </th>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Started
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Assistant Steps
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Tool Calls
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Tokens (In)
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Tokens (Out)
                        </th>
                        <th className="px-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Total Cost
                        </th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {report.conversations.map((conv) => {
                        const totalIn =
                          conv.promptTokens +
                          conv.cachedTokens +
                          conv.reasoningTokens;
                        const totalOut = conv.completionTokens;
                        return (
                          <tr
                            key={conv.conversationId}
                            className="hover:bg-gray-50 cursor-pointer"
                            onClick={() => setSelectedConversation(conv)}
                          >
                            <td className="px-4 py-3 whitespace-nowrap text-sm font-medium text-gray-900">
                              {conv.title || '(Untitled conversation)'}
                            </td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-700">
                              {formatDate(conv.created)}
                            </td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                              {conv.assistantSteps}
                            </td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                              {conv.totalToolCalls}
                            </td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                              {formatTokens(totalIn)}
                            </td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                              {formatTokens(totalOut)}
                            </td>
                            <td className="px-4 py-3 whitespace-nowrap text-sm text-right text-gray-700">
                              {formatCurrency(conv.totalCost)}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
                {report.conversations.length < conversationTotalCount && (
                  <div className="px-4 py-3 border-t border-gray-200 bg-gray-50 flex justify-end">
                    <button
                      type="button"
                      onClick={loadMoreConversations}
                      disabled={loadingConversations}
                      className="px-3 py-1.5 text-sm rounded border border-gray-300 bg-white hover:bg-gray-50 disabled:opacity-50"
                    >
                      {loadingConversations ? 'Loading...' : 'Load more'}
                    </button>
                  </div>
                )}
              </div>
            )}

            {conversationsError && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-4">
                <div className="text-red-700">{conversationsError}</div>
              </div>
            )}

            {loadingConversations && report.conversations.length === 0 && (
              <div className="bg-white rounded-lg border border-gray-200 p-6 text-sm text-gray-500">
                Loading conversations...
              </div>
            )}
          </div>
        )}

        {/* Empty State */}
        {!loading && !error && report && report.totalConversations === 0 && (
          <div className="bg-white rounded-lg border border-gray-200 p-8 text-center">
            <svg className="w-12 h-12 mx-auto mb-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
            </svg>
            <p className="text-gray-600">No conversations found for this {isAssistant ? 'assistant' : 'guide'} in the selected period.</p>
            <p className="text-sm text-gray-500 mt-2">Try selecting a different date range.</p>
          </div>
        )}
      </div>
      {selectedConversation && (
        <ConversationDetailPanel
          conversation={selectedConversation}
          allowedAssistantIds={allowedAssistantIds}
          onClose={() => setSelectedConversation(null)}
        />
      )}
    </div>
  );
}
