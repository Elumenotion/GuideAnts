import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FaHeartbeat, FaSpinner, FaSyncAlt } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { api } from '../../../services/api';
import {
  InfrastructureProbeRequestItemDto,
  InfrastructureProbeResultDto,
  SettingsRuntimeDependencyDto,
  SettingsRuntimeDependencyKind,
  SettingsRuntimeDependencySource,
} from '../../../types/settings';
import { IconActionButton, TextActionButton } from './shared/ActionButtons';
import { getRuntimeDependencyDisplayName } from '../constants/displayLabels';

interface InfrastructureTabProps {
  /**
   * Phase E deep-link from Connections → Infrastructure. When set, the matching
   * row is scrolled into view and briefly highlighted so operators can confirm
   * what the referring tab was talking about.
   */
  focusedRuntimeKey?: string | null;
  onFocusedRuntimeKeyHandled?: () => void;
}

const sourceBadgeClasses: Record<string, string> = {
  appsettings: 'bg-blue-50 text-blue-700 ring-blue-600/20',
  environment: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
  compose: 'bg-purple-50 text-purple-700 ring-purple-600/20',
  'user-secrets': 'bg-amber-50 text-amber-800 ring-amber-600/20',
  'application-settings': 'bg-sky-50 text-sky-900 ring-sky-600/20',
  unknown: 'bg-gray-100 text-gray-700 ring-gray-500/20',
};

const sourceLabels: Record<string, string> = {
  appsettings: 'appsettings',
  environment: 'env var',
  compose: 'compose',
  'user-secrets': 'user-secrets',
  'application-settings': 'database',
  unknown: 'unknown',
};

function SourceBadge({ source }: { source: string }) {
  const cls = sourceBadgeClasses[source] ?? sourceBadgeClasses.unknown;
  const label = sourceLabels[source] ?? source;
  return (
    <span className={`inline-flex items-center rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${cls}`}>
      {label}
    </span>
  );
}

function truncate(value: string, max = 72): string {
  return value.length > max ? `${value.slice(0, max - 1)}…` : value;
}

/**
 * R-5.7 prefix check for LlamaCpp:BaseUrl — surfaces a warning when the stored
 * value doesn't look like an http(s) URL so operators can fix a misconfigured
 * base before the first chat turn hits it.
 */
function isLlamaCppPrefixIssue(dep: SettingsRuntimeDependencyDto): boolean {
  if (dep.key !== 'LlamaCpp:BaseUrl' || !dep.hasValue) {
    return false;
  }
  const value = dep.currentValue ?? '';
  return !/^https?:\/\//i.test(value);
}

function describeProbe(result: InfrastructureProbeResultDto | undefined): {
  label: string;
  tone: 'ok' | 'warn' | 'err' | 'pending';
  detail?: string;
} {
  if (!result) {
    return { label: 'Not probed', tone: 'pending' };
  }
  if (result.kind === 'url') {
    if (result.reachable) {
      return {
        label: `Reachable${result.statusCode != null ? ` (HTTP ${result.statusCode})` : ''}`,
        tone: 'ok',
        detail: result.latencyMs != null ? `${result.latencyMs} ms` : undefined,
      };
    }
    return {
      label: 'Unreachable',
      tone: 'err',
      detail: result.error ?? undefined,
    };
  }
  if (result.kind === 'path') {
    if (result.exists && result.writable) {
      return { label: 'Exists · writable', tone: 'ok' };
    }
    if (result.exists && !result.writable) {
      return { label: 'Exists · not writable', tone: 'warn', detail: result.error ?? undefined };
    }
    return { label: 'Missing', tone: 'err', detail: result.error ?? undefined };
  }
  return { label: result.error ?? 'Unknown', tone: 'err', detail: result.error ?? undefined };
}

function ProbeBadge({ result }: { result: InfrastructureProbeResultDto | undefined }) {
  const { label, tone, detail } = describeProbe(result);
  const toneClasses: Record<string, string> = {
    ok: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
    warn: 'bg-amber-50 text-amber-800 ring-amber-600/20',
    err: 'bg-red-50 text-red-700 ring-red-600/20',
    pending: 'bg-gray-100 text-gray-600 ring-gray-500/20',
  };
  return (
    <div className="flex flex-col gap-0.5">
      <span
        className={`inline-flex w-fit items-center rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${toneClasses[tone]}`}
      >
        {label}
      </span>
      {detail && <span className="text-xs text-gray-500">{detail}</span>}
    </div>
  );
}

function kindHint(kind: SettingsRuntimeDependencyKind | string): string {
  if (kind === 'url') return 'URL';
  if (kind === 'path') return 'Path';
  return 'Value';
}

export function InfrastructureTab({ focusedRuntimeKey, onFocusedRuntimeKeyHandled }: InfrastructureTabProps) {
  const [dependencies, setDependencies] = useState<SettingsRuntimeDependencyDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [probeResults, setProbeResults] = useState<Record<string, InfrastructureProbeResultDto>>({});
  const [inFlightKey, setInFlightKey] = useState<string | null>(null);
  const [highlightKey, setHighlightKey] = useState<string | null>(null);

  const rowRefs = useRef<Map<string, HTMLTableRowElement>>(new Map());

  const loadDependencies = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const list = await api.settings.infrastructure.listDependencies();
      setDependencies(list);
    } catch (error) {
      setLoadError(error instanceof Error ? error.message : 'Failed to load infrastructure dependencies.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadDependencies();
  }, [loadDependencies]);

  const runProbes = useCallback(
    async (deps: SettingsRuntimeDependencyDto[], source: 'auto' | 'user') => {
      const probeItems: InfrastructureProbeRequestItemDto[] = deps
        .filter((dep) => dep.hasValue && (dep.kind === 'url' || dep.kind === 'path'))
        .map((dep) => ({
          id: dep.key,
          kind: dep.kind,
          target: dep.currentValue ?? '',
        }));

      if (probeItems.length === 0) {
        return;
      }

      try {
        const batch = await api.settings.infrastructure.probe(probeItems);
        setProbeResults((previous) => {
          const next = { ...previous };
          for (const result of batch.results) {
            next[result.id] = result;
          }
          return next;
        });
      } catch (error) {
        if (source === 'user') {
          const message = error instanceof Error ? error.message : 'Probe request failed.';
          setLoadError(message);
        }
      }
    },
    []
  );

  // R-5.7 auto-probe: on mount, kick off URL probes only. Path probes are
  // user-triggered because filesystem writability can be expensive on network
  // mounts and isn't useful to an operator who just opened the tab.
  useEffect(() => {
    if (dependencies.length === 0) {
      return;
    }
    const urlDeps = dependencies.filter((d) => d.kind === 'url' && d.hasValue);
    if (urlDeps.length === 0) {
      return;
    }
    void runProbes(urlDeps, 'auto');
  }, [dependencies, runProbes]);

  const handleProbeOne = useCallback(
    async (dep: SettingsRuntimeDependencyDto) => {
      if (!dep.hasValue) {
        return;
      }
      setInFlightKey(dep.key);
      try {
        await runProbes([dep], 'user');
      } finally {
        setInFlightKey(null);
      }
    },
    [runProbes]
  );

  const handleProbeAll = useCallback(() => {
    void runProbes(dependencies, 'user');
  }, [dependencies, runProbes]);

  // Deep-link focus behavior: scroll to the target row and briefly highlight.
  useEffect(() => {
    if (!focusedRuntimeKey || dependencies.length === 0) {
      return;
    }
    const node = rowRefs.current.get(focusedRuntimeKey);
    if (!node) {
      return;
    }
    node.scrollIntoView({ block: 'center', behavior: 'smooth' });
    setHighlightKey(focusedRuntimeKey);
    const timeoutId = window.setTimeout(() => {
      setHighlightKey(null);
      onFocusedRuntimeKeyHandled?.();
    }, 2000);
    return () => window.clearTimeout(timeoutId);
  }, [focusedRuntimeKey, dependencies, onFocusedRuntimeKeyHandled]);

  const sortedDependencies = useMemo(
    () =>
      [...dependencies].sort((left, right) =>
        getRuntimeDependencyDisplayName(left.key).localeCompare(getRuntimeDependencyDisplayName(right.key))
      ),
    [dependencies]
  );

  if (loading) {
    return (
      <section className="rounded-lg border border-gray-200 bg-white p-8">
        <LoadingSpinner message="Loading infrastructure dependencies..." />
      </section>
    );
  }

  return (
    <section className="overflow-hidden rounded-lg border border-gray-200 bg-white">
      <div className="flex flex-col gap-3 border-b border-gray-200 px-6 py-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-base font-semibold text-gray-900">Infrastructure</h2>
          <p className="mt-1 text-sm text-gray-600">
            Runtime-owned dependency keys with source tracking and reachability diagnostics. Read-only — change values in
            appsettings, environment variables, or docker-compose and refresh.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <TextActionButton
            tone="neutral"
            icon={<FaSyncAlt />}
            onClick={() => void loadDependencies()}
            title="Reload runtime dependencies."
          >
            Refresh
          </TextActionButton>
          <TextActionButton
            tone="primary"
            icon={<FaHeartbeat />}
            onClick={handleProbeAll}
            title="Run every dependency probe."
          >
            Probe all
          </TextActionButton>
        </div>
      </div>

      {loadError && (
        <div className="border-b border-red-200 bg-red-50 px-6 py-3 text-sm text-red-700">
          <div className="font-medium">Infrastructure request failed</div>
          <div className="mt-0.5">{loadError}</div>
        </div>
      )}

      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Key</th>
              <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Value</th>
              <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Source</th>
              <th className="px-4 py-3 text-left text-xs font-medium uppercase tracking-wide text-gray-500">Diagnostic</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-200 bg-white">
            {sortedDependencies.length === 0 && (
              <tr>
                <td colSpan={4} className="px-4 py-6 text-center text-sm text-gray-500">
                  No runtime-owned dependencies are registered.
                </td>
              </tr>
            )}
            {sortedDependencies.map((dep) => {
              const probeResult = probeResults[dep.key];
              const displayName = getRuntimeDependencyDisplayName(dep.key);
              const showPrefixWarning = isLlamaCppPrefixIssue(dep);
              const canProbe = dep.hasValue && (dep.kind === 'url' || dep.kind === 'path');
              const isHighlighted = highlightKey === dep.key;
              return (
                <tr
                  key={dep.key}
                  ref={(element) => {
                    if (element) {
                      rowRefs.current.set(dep.key, element);
                    } else {
                      rowRefs.current.delete(dep.key);
                    }
                  }}
                  className={isHighlighted ? 'bg-yellow-50 transition-colors' : undefined}
                >
                  <td className="px-4 py-3 align-top text-sm">
                    <div className="font-mono text-gray-900">{dep.key}</div>
                    <div className="mt-0.5 text-xs text-gray-500">{displayName}</div>
                    {dep.usedByProviderIds.length > 0 && (
                      <div className="mt-1 flex flex-wrap gap-1">
                        {dep.usedByProviderIds.map((providerId) => (
                          <span key={providerId} className="rounded bg-gray-100 px-1.5 py-0.5 text-[11px] text-gray-600">
                            {providerId}
                          </span>
                        ))}
                      </div>
                    )}
                  </td>
                  <td className="px-4 py-3 align-top text-sm text-gray-700">
                    {dep.isSecret && dep.hasValue ? (
                      <span className="font-mono text-gray-500">••••••••</span>
                    ) : dep.hasValue ? (
                      <span className="font-mono text-gray-800" title={dep.currentValue ?? ''}>
                        {truncate(dep.currentValue ?? '')}
                      </span>
                    ) : (
                      <span className="text-red-700">Missing</span>
                    )}
                    <div className="mt-0.5 text-xs text-gray-500">{kindHint(dep.kind)}</div>
                  </td>
                  <td className="px-4 py-3 align-top text-sm">
                    <SourceBadge source={(dep.source as SettingsRuntimeDependencySource) ?? 'unknown'} />
                  </td>
                  <td className="px-4 py-3 align-top text-sm">
                    <div className="space-y-1.5">
                      {showPrefixWarning && (
                        <div className="rounded border border-amber-300 bg-amber-50 px-2 py-1 text-xs text-amber-800">
                          LlamaCpp:BaseUrl must start with <span className="font-mono">http://</span> or{' '}
                          <span className="font-mono">https://</span>.
                        </div>
                      )}
                      <ProbeBadge result={probeResult} />
                      {canProbe && (
                        <IconActionButton
                          label={dep.kind === 'path' ? 'Check' : 'Probe'}
                          tone="info"
                          icon={inFlightKey === dep.key ? <FaSpinner className="animate-spin" /> : <FaHeartbeat />}
                          disabled={inFlightKey === dep.key}
                          onClick={() => void handleProbeOne(dep)}
                          title={`${dep.kind === 'path' ? 'Check path for' : 'Probe'} ${displayName}`}
                        />
                      )}
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}
