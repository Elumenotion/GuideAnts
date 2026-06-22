import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../services/api';
import { UsageBucket, UsageSummaryDto, ProjectUsageSummaryDto, UsageBreakdownWithCategoriesDto } from '../types/usage';
import { HeaderActionsBar } from '../components/common/HeaderActionsBar';
import { GuideAntsGuideButton } from '../features/guideantsGuide/GuideAntsGuideButton';
import { HomeButton } from '../components/common/HomeButton';
import { SettingsButton } from '../components/common/SettingsButton';
import { TourStartButton } from '../tour/TourStartButton';
import { useRegisterTour } from '../tour/useRegisterTour';

type RangePreset = '7d' | '30d' | '90d' | 'custom';



// removed unused toIsoUtc helper after switching to explicit UTC bounds

const Usage = () => {
  const formatCurrency = (value: number, currency?: string) =>
    new Intl.NumberFormat('en-US', { style: 'currency', currency: currency || 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value);

  const [range, setRange] = useState<RangePreset>('30d');
  const [bucket, setBucket] = useState<UsageBucket>('day');
  const [from, to] = useMemo((): [string, string] => {
    const now = new Date();
    // compute start at 00:00:00 UTC N days ago
    const startUtcMidnight = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
    if (range === '7d') startUtcMidnight.setUTCDate(startUtcMidnight.getUTCDate() - 7);
    else if (range === '30d') startUtcMidnight.setUTCDate(startUtcMidnight.getUTCDate() - 30);
    else if (range === '90d') startUtcMidnight.setUTCDate(startUtcMidnight.getUTCDate() - 90);
    return [startUtcMidnight.toISOString(), now.toISOString()];
  }, [range]);

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [summary, setSummary] = useState<UsageSummaryDto | null>(null);
  const [projectSummaries, setProjectSummaries] = useState<ProjectUsageSummaryDto[] | null>(null);
  const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
  const [breakdown, setBreakdown] = useState<UsageBreakdownWithCategoriesDto | null>(null);
  const categoryRef = useRef<HTMLDivElement | null>(null);
  const [categoryHeight, setCategoryHeight] = useState<number>(0);
  
  useEffect(() => {
    const recalc = () => {
      const el = categoryRef.current;
      if (!el) {
        setCategoryHeight(0);
        return;
      }
      const rect = el.getBoundingClientRect();
      const available = Math.max(200, Math.floor(window.innerHeight - rect.top - 24));
      setCategoryHeight(available);
    };
    recalc();
    window.addEventListener('resize', recalc);
    return () => window.removeEventListener('resize', recalc);
  }, [summary, projectSummaries, breakdown, range, bucket, selectedProjectId]);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError(null);
      try {
        if (!selectedProjectId) {
          const [overall, byProject] = await Promise.all([
            api.usage.getSummary(from, to, bucket),
            api.usage.getSummaryByProject(from, to),
          ]);
          if (!cancelled) {
            setSummary(overall);
            setProjectSummaries(byProject);
            const bd = await api.usage.getBreakdown(from, to);
            if (!cancelled) setBreakdown(bd);
          }
        } else {
          const [proj, bd] = await Promise.all([
            api.usage.getProjectSummary(selectedProjectId, from, to, bucket),
            api.usage.getProjectBreakdown(selectedProjectId, from, to)
          ]);
          if (!cancelled) { setSummary(proj); setBreakdown(bd); }
        }
      } catch (e: any) {
        if (!cancelled) setError(e?.message ?? 'Failed to load usage');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => {
      cancelled = true;
    };
  }, [from, to, bucket, selectedProjectId]);

  const totalCost = summary?.totals.totalCostUsd ?? 0;
  const totalEvents = summary?.totals.totalEvents ?? 0;

  useRegisterTour('usage', [
    {
      target: '[data-tour-id="usage.range"]',
      content: 'Select a <strong>date range</strong> for your usage.'
    },
    {
      target: '[data-tour-id="usage.bucket"]',
      content: 'Choose how to <strong>group</strong> usage over time (day/week/month).'
    },
    {
      target: '[data-tour-id="usage.billing.info"]',
      content: 'See your <strong>plan</strong>, <strong>credits</strong>, and <strong>next billing</strong> information.'
    },
    {
      target: '[data-tour-id="usage.time-series"]',
      content: 'The <strong>time series</strong> shows cost over time.'
    },
    {
      target: '[data-tour-id="usage.projects.grid"]',
      content: 'View usage by <strong>project</strong> and drill into a single project.'
    },
    {
      target: '[data-tour-id="usage.categories.list"]',
      content: 'Breakdown by <strong>category</strong> helps you understand where costs accrue.'
    }
  ]);

  return (
    <div className="h-full overflow-auto bg-gray-50 p-8">
      <div className="max-w-7xl mx-auto">
        <div className="mb-6 flex items-center justify-between gap-2">
          <div className="flex min-w-0 flex-1 items-center">
            <img src="./guide.png" alt="GuideAnts" className="w-12 h-12 mr-4 shrink-0" />
            <div className="min-w-0">
              <h1 className="text-xl font-semibold">Usage</h1>
              <p className="text-sm text-gray-600">Track activity and costs across your owned projects.</p>
            </div>
          </div>
          <HeaderActionsBar>
            <GuideAntsGuideButton />
            <HomeButton />
            <SettingsButton />
            <TourStartButton screenId="usage" inline />
          </HeaderActionsBar>
        </div>

        <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-2">
            <select
              className="border border-gray-300 rounded px-2 py-1 text-sm"
              value={range}
              onChange={e => setRange(e.target.value as RangePreset)}
              data-tour-id="usage.range"
            >
              <option value="7d">Last 7 days</option>
              <option value="30d">Last 30 days</option>
              <option value="90d">Last 90 days</option>
            </select>
            <select
              className="border border-gray-300 rounded px-2 py-1 text-sm"
              value={bucket}
              onChange={e => setBucket(e.target.value as UsageBucket)}
              data-tour-id="usage.bucket"
            >
              <option value="day">Day</option>
              <option value="week">Week</option>
              <option value="month">Month</option>
            </select>
          </div>
        </div>

        {selectedProjectId && (
          <div className="mb-3">
            <button
              className="text-xs text-blue-600 hover:underline"
              onClick={() => setSelectedProjectId(null)}
            >
              ← All projects
            </button>
          </div>
        )}

        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <div className="rounded-lg border border-gray-200 bg-white p-4">
            <div className="text-sm font-medium text-gray-900">Total Cost</div>
            <div className="mt-2 text-2xl font-semibold">{formatCurrency(totalCost)}</div>
            <div className="mt-1 text-xs text-gray-600">{totalEvents} events</div>
          </div>

          <div className="rounded-lg border border-gray-200 bg-white p-4 col-span-2" data-tour-id="usage.time-series">
            <div className="text-sm font-medium text-gray-900 mb-2">Time Series (UTC)</div>
            <div className="overflow-x-auto">
              <div className="flex items-end gap-1 h-24 min-w-max">
                {summary?.timeSeries.map(pt => {
                  const max = Math.max(...summary.timeSeries.map(x => x.totalCostUsd), 1);
                  const height = Math.max(4, Math.round((pt.totalCostUsd / max) * 88));
                  return (
                    <div key={pt.periodStart} className="flex flex-col items-center">
                      <div className="bg-blue-500 w-3 rounded" style={{ height }} />
                      <div className="text-[10px] text-gray-500 mt-1 whitespace-nowrap">
                        {new Intl.DateTimeFormat(undefined, { 
                          month: 'short', 
                          day: 'numeric', 
                          timeZone: 'UTC' 
                        }).format(new Date(pt.periodStart))}
                      </div>
                    </div>
                  );
                })}
                {!summary?.timeSeries?.length && (
                  <div className="text-xs text-gray-500">No data</div>
                )}
              </div>
            </div>
          </div>
        </div>

        {!selectedProjectId && (
          <div className="mt-3 rounded-lg border border-gray-200 bg-white p-4" data-tour-id="usage.projects.grid">
            <div className="text-sm font-medium text-gray-900 mb-2">Projects (Owned)</div>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-2">
              {projectSummaries?.map(p => (
                <button
                  key={p.projectId}
                  onClick={() => setSelectedProjectId(p.projectId)}
                  className="text-left rounded border border-gray-200 p-3 hover:bg-gray-50"
                >
                  <div className="text-sm font-medium text-gray-900 truncate">{p.projectTitle}</div>
                  <div className="text-xs text-gray-600">{formatCurrency(p.totalCostUsd)} • {p.totalEvents} events</div>
                </button>
              ))}
              {!projectSummaries?.length && (
                <div className="text-xs text-gray-500">No owned projects found.</div>
              )}
            </div>
          </div>
        )}

        {/* Removed legacy breakdown sections for clarity; category grouping supersedes them */}

        <div ref={categoryRef} className="mt-3 rounded-lg border border-gray-200 bg-white p-4 flex flex-col overflow-hidden" style={{ height: categoryHeight || undefined }} data-tour-id="usage.categories.list">
          <div className="text-sm font-medium text-gray-900 mb-2 shrink-0">By Category</div>
          <div className="space-y-1 overflow-y-auto flex-1 pr-2">
            {loading ? (
              <div className="text-xs text-gray-500">Loading…</div>
            ) : error ? (
              <div className="text-xs text-red-600">{error}</div>
            ) : breakdown?.byCategory.length ? (
              breakdown.byCategory.map(c => (
                <div key={`${c.id}:${c.key}`} className="flex justify-between text-sm">
                  <div className="min-w-0 pr-2">
                    <div className="text-gray-900 truncate">{c.title}</div>
                    {c.description && <div className="text-[11px] text-gray-500 truncate">{c.description}</div>}
                  </div>
                  <div className="text-gray-900 font-medium">{formatCurrency(c.totalCostUsd)}</div>
                </div>
              ))
            ) : (
              <div className="text-xs text-gray-500">No data</div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};

export default Usage;


