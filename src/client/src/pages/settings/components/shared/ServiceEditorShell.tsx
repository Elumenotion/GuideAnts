import type { ReactNode } from 'react';
import { ReadinessBadge } from './ReadinessBadge';

interface ServiceEditorShellProps {
  serviceName: string;
  /** Persisted (server) active provider display name */
  activeProviderLabel: string;
  /** When set, user is editing another provider before save */
  editingProviderLabel?: string | null;
  readinessStatus: 'ready' | 'blocked' | 'not-configured' | string;
  readinessSummary: string;
  providerSelector: ReactNode;
  providerSettings: ReactNode;
  serviceSettings?: ReactNode;
  actions: ReactNode;
}

export function ServiceEditorShell({
  serviceName,
  activeProviderLabel,
  editingProviderLabel,
  readinessStatus,
  readinessSummary,
  providerSelector,
  providerSettings,
  serviceSettings,
  actions,
}: ServiceEditorShellProps) {
  const isNotConfigured = activeProviderLabel === 'Not configured';
  const effectiveReadinessStatus = isNotConfigured ? 'not-configured' : readinessStatus;
  const effectiveReadinessSummary = isNotConfigured
    ? 'No active provider configured yet. Select a provider and save to activate it.'
    : readinessSummary;

  return (
    <div className="space-y-4">
      <section className="rounded border border-gray-200 p-4">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">{serviceName}</h2>
            <p className="mt-1 text-sm text-gray-600">Active provider: {activeProviderLabel}</p>
            {editingProviderLabel ? (
              <p className="mt-1 text-sm text-amber-800">
                Editing configuration for: <span className="font-medium">{editingProviderLabel}</span> (not active until you save)
              </p>
            ) : null}
            <p className="mt-1 text-xs text-gray-500">{effectiveReadinessSummary}</p>
          </div>
          <ReadinessBadge status={effectiveReadinessStatus} />
        </div>
      </section>

      <section className="rounded border border-gray-200 p-4">
        <h3 className="mb-3 text-sm font-semibold text-gray-900">Provider</h3>
        {providerSelector}
      </section>

      <section className="rounded border border-gray-200 p-4">
        <h3 className="mb-3 text-sm font-semibold text-gray-900">Provider Configuration</h3>
        {providerSettings}
      </section>

      {serviceSettings ? (
        <section className="rounded border border-gray-200 p-4">
          <h3 className="mb-3 text-sm font-semibold text-gray-900">Service Settings</h3>
          {serviceSettings}
        </section>
      ) : null}

      <div className="flex items-center justify-end gap-2">{actions}</div>
    </div>
  );
}
