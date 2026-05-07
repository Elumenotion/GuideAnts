import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FaHeartbeat, FaSave, FaSpinner, FaSyncAlt, FaUndo } from 'react-icons/fa';
import LoadingSpinner from '../../../components/LoadingSpinner';
import { useToast } from '../../../components/common/Toast';
import { api } from '../../../services/api';
import {
  InfrastructureProbeRequestItemDto,
  InfrastructureProbeResultDto,
  SettingsRuntimeDependencyDto,
} from '../../../types/settings';
import { TextActionButton } from './shared/ActionButtons';
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

function validateDependencyDraft(dep: SettingsRuntimeDependencyDto, draftValue: string): string | null {
  const value = draftValue.trim();
  if (value.length === 0) {
    return null;
  }

  if (dep.kind !== 'url') {
    return null;
  }

  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    return 'Must be an absolute URL.';
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return 'Must use http:// or https://.';
  }

  if (dep.key === 'LlamaCpp:BaseUrl') {
    const normalizedPath = parsed.pathname.replace(/\/+$/, '') || '/';
    if (normalizedPath.toLowerCase() !== '/llama-cpp') {
      return "Must include the '/llama-cpp' path.";
    }
  }

  return null;
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
    <div className="flex flex-wrap items-center gap-2">
      <span
        className={`inline-flex w-fit items-center rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset ${toneClasses[tone]}`}
      >
        {label}
      </span>
      {detail && <span className="text-xs text-gray-500">{detail}</span>}
    </div>
  );
}

export function InfrastructureTab({ focusedRuntimeKey, onFocusedRuntimeKeyHandled }: InfrastructureTabProps) {
  const { showToast } = useToast();
  const [dependencies, setDependencies] = useState<SettingsRuntimeDependencyDto[]>([]);
  const [draftValues, setDraftValues] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [probeResults, setProbeResults] = useState<Record<string, InfrastructureProbeResultDto>>({});
  const [inFlightKey, setInFlightKey] = useState<string | null>(null);
  const [savingKey, setSavingKey] = useState<string | null>(null);
  const [highlightKey, setHighlightKey] = useState<string | null>(null);

  const rowRefs = useRef<Map<string, HTMLElement>>(new Map());

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

  useEffect(() => {
    setDraftValues(
      Object.fromEntries(
        dependencies.map((dependency) => [dependency.key, dependency.currentValue ?? ''])
      )
    );
  }, [dependencies]);

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

  const updateDraft = useCallback((key: string, value: string) => {
    setDraftValues((previous) => ({ ...previous, [key]: value }));
  }, []);

  const resetDraft = useCallback((dependency: SettingsRuntimeDependencyDto) => {
    setDraftValues((previous) => ({ ...previous, [dependency.key]: dependency.currentValue ?? '' }));
  }, []);

  const saveDependency = useCallback(
    async (dependency: SettingsRuntimeDependencyDto) => {
      if (dependency.readOnly) {
        return;
      }

      const rawDraft = draftValues[dependency.key] ?? '';
      const draftError = validateDependencyDraft(dependency, rawDraft);
      if (draftError) {
        showToast({
          type: 'error',
          title: 'Invalid infrastructure value',
          message: `${dependency.key}: ${draftError}`,
        });
        return;
      }

      const nextValue = rawDraft.trim();
      setSavingKey(dependency.key);
      setLoadError(null);
      try {
        await api.settings.infrastructure.updateDependency(
          dependency.key,
          nextValue.length > 0 ? nextValue : null
        );

        await loadDependencies();
        showToast({
          type: 'success',
          title: 'Infrastructure override saved',
          message:
            nextValue.length > 0
              ? `${dependency.key} now uses the database override value.`
              : `${dependency.key} override cleared. Runtime now falls back to env/appsettings.`,
        });
      } catch (error) {
        const status = (error as { status?: number })?.status;
        showToast({
          type: 'error',
          title: status === 409 ? 'Infrastructure settings changed elsewhere' : 'Failed to save infrastructure override',
          message:
            status === 409
              ? 'Refresh and retry so your edit applies to the latest row version.'
              : error instanceof Error
                ? error.message
                : 'The infrastructure update request failed.',
        });
      } finally {
        setSavingKey(null);
      }
    },
    [draftValues, loadDependencies, showToast]
  );

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
            Edit local runtime endpoints and check whether each target is responding.
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

      <div className="divide-y divide-gray-200">
        <div className="hidden px-6 py-3 text-xs font-medium uppercase tracking-wide text-gray-500 lg:grid lg:grid-cols-[minmax(0,0.95fr)_minmax(0,1.35fr)_220px] lg:gap-6">
          <div>Setting</div>
          <div>Value</div>
          <div>Health</div>
        </div>

        {sortedDependencies.length === 0 && (
          <div className="px-6 py-8 text-center text-sm text-gray-500">
            No runtime-owned dependencies are registered.
          </div>
        )}

        {sortedDependencies.map((dep) => {
          const probeResult = probeResults[dep.key];
          const displayName = getRuntimeDependencyDisplayName(dep.key);
          const showPrefixWarning = isLlamaCppPrefixIssue(dep);
          const canProbe = dep.hasValue && (dep.kind === 'url' || dep.kind === 'path');
          const isEditable = !dep.readOnly && !dep.isSecret;
          const draftValue = draftValues[dep.key] ?? '';
          const draftValidationError = isEditable ? validateDependencyDraft(dep, draftValue) : null;
          const isDirty = isEditable && draftValue.trim() !== (dep.currentValue ?? '').trim();
          const isSaving = savingKey === dep.key;
          const isHighlighted = highlightKey === dep.key;

          return (
            <article
              key={dep.key}
              ref={(element) => {
                if (element) {
                  rowRefs.current.set(dep.key, element);
                } else {
                  rowRefs.current.delete(dep.key);
                }
              }}
              className={`px-6 py-5 transition-colors ${isHighlighted ? 'bg-yellow-50' : 'bg-white'}`}
            >
              <div className="grid gap-5 lg:grid-cols-[minmax(0,0.95fr)_minmax(0,1.35fr)_220px] lg:gap-6">
                <div className="min-w-0">
                  <div className="text-sm font-semibold leading-6 text-gray-900">{displayName}</div>
                  <div className="mt-1 break-all font-mono text-xs leading-5 text-gray-500">{dep.key}</div>
                </div>

                <div className="min-w-0">
                  {isEditable ? (
                    <div className="space-y-3">
                      <input
                        type="text"
                        className="w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-sm text-gray-800 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-200"
                        value={draftValue}
                        onChange={(event) => updateDraft(dep.key, event.target.value)}
                        placeholder={dep.currentValue ?? ''}
                        aria-label={`${displayName} value`}
                        disabled={isSaving}
                      />
                      <div className="flex flex-wrap items-center gap-2">
                        <TextActionButton
                          tone="primary"
                          icon={isSaving ? <FaSpinner className="animate-spin" /> : <FaSave />}
                          disabled={!isDirty || isSaving || !!draftValidationError}
                          onClick={() => void saveDependency(dep)}
                          title={`Save ${displayName} override`}
                        >
                          Save
                        </TextActionButton>
                        <TextActionButton
                          tone="neutral"
                          icon={<FaUndo />}
                          disabled={!isDirty || isSaving}
                          onClick={() => resetDraft(dep)}
                          title={`Reset ${displayName} draft`}
                        >
                          Reset
                        </TextActionButton>
                        {isDirty && <span className="text-xs font-medium uppercase tracking-wide text-amber-700">Unsaved</span>}
                        {draftValidationError && (
                          <span className="text-xs font-medium text-red-700">{draftValidationError}</span>
                        )}
                      </div>
                    </div>
                  ) : dep.isSecret && dep.hasValue ? (
                    <span className="font-mono text-gray-500">••••••••</span>
                  ) : dep.hasValue ? (
                    <span className="font-mono text-gray-800" title={dep.currentValue ?? ''}>
                      {truncate(dep.currentValue ?? '')}
                    </span>
                  ) : (
                    <span className="text-red-700">Missing</span>
                  )}
                </div>

                <div className="min-w-0">
                  <div className="space-y-3">
                    {showPrefixWarning && (
                      <div className="rounded border border-amber-300 bg-amber-50 px-2.5 py-2 text-xs text-amber-800">
                        Must start with <span className="font-mono">http://</span> or <span className="font-mono">https://</span>.
                      </div>
                    )}
                    <ProbeBadge result={probeResult} />
                    {canProbe && (
                      <TextActionButton
                        tone="info"
                        icon={inFlightKey === dep.key ? <FaSpinner className="animate-spin" /> : <FaHeartbeat />}
                        disabled={inFlightKey === dep.key}
                        onClick={() => void handleProbeOne(dep)}
                        title={`${dep.kind === 'path' ? 'Check' : 'Probe'} ${displayName}`}
                      >
                        {dep.kind === 'path' ? 'Check' : 'Probe'}
                      </TextActionButton>
                    )}
                  </div>
                </div>
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}
