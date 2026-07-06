import { forwardRef, useEffect, useImperativeHandle, useMemo, type ReactNode } from 'react';
import { OperationalDependencyRow } from '../../../../pages/settings/components/shared/OperationalDependencyRow';
import { ProviderFieldsSection } from '../../../../pages/settings/components/shared/ProviderFieldsSection';
import { getServiceProviderDisplayName } from '../../../../pages/settings/constants/displayLabels';
import { useServiceEditorController } from '../../../../pages/settings/state/useServiceEditorController';
import type { LocalRuntimeReadinessState } from '../../../../pages/settings/editors/common/localOperationPolling';
import type { OptionalServiceKey } from '../types';

export interface LocalAiServiceStepHandle {
  persist: () => Promise<void>;
}

interface LocalAiServiceStepBaseProps {
  serviceId: OptionalServiceKey;
  title: string;
  description: string;
  localProviderId: string;
  requireRuntimeReadiness?: boolean;
  manager?: ReactNode;
  runtimeReadiness?: LocalRuntimeReadinessState | null;
  behaviorContent?: ReactNode;
}

export const LocalAiServiceStepBase = forwardRef<LocalAiServiceStepHandle, LocalAiServiceStepBaseProps>(
  function LocalAiServiceStepBase(
    {
      serviceId,
      title,
      description,
      localProviderId,
      requireRuntimeReadiness = false,
      manager,
      runtimeReadiness,
      behaviorContent,
    },
    ref
  ) {
    const {
      state,
      loading,
      error,
      fieldErrors,
      draft,
      save,
      clearFieldError,
    } = useServiceEditorController(serviceId);

    const localProvider = useMemo(
      () => state?.providers.find((provider) => provider.providerId === localProviderId),
      [localProviderId, state]
    );

    useEffect(() => {
      if (!state || !localProvider) {
        return;
      }
      if (draft.activeProviderId !== localProviderId) {
        draft.switchProvider(localProviderId);
      }
    }, [draft, localProvider, localProviderId, state]);

    useImperativeHandle(
      ref,
      () => ({
        persist: async () => {
          // All AI services are optional. Runtime readiness (model loaded + warmed)
          // must NOT block wizard navigation — it is surfaced as an informational
          // "Readiness" banner instead. Only real, user-entered field validation may
          // block, via save() below.
          if (!localProvider) {
            throw new Error(`Local provider '${localProviderId}' is not available for ${title}.`);
          }
          if (draft.activeProviderId !== localProviderId) {
            draft.switchProvider(localProviderId);
          }
          const ok = await save();
          if (!ok) {
            throw new Error(`Fix ${title} fields before continuing.`);
          }
        },
      }),
      [draft, localProvider, localProviderId, save, title]
    );

    if (loading) {
      return <div className="text-sm text-gray-600">Loading {title} settings…</div>;
    }

    if (!state || !localProvider) {
      return (
        <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          {error ?? `Local provider '${localProviderId}' is not available for ${title}.`}
        </div>
      );
    }

    const localDraft = draft.draftsByProvider[localProviderId] ?? {};
    const serviceReadinessBlocked = state.readiness.status !== 'ready';
    const runtimeReadinessPending = requireRuntimeReadiness && runtimeReadiness === null;
    const runtimeReadinessMissing = requireRuntimeReadiness && runtimeReadiness === undefined;
    const runtimeReadinessBlocked = requireRuntimeReadiness && (runtimeReadinessMissing || runtimeReadinessPending || !runtimeReadiness?.ready);
    const readinessBlocked = serviceReadinessBlocked || runtimeReadinessBlocked;
    const runtimeDetail = runtimeReadiness?.detail?.trim();
    const readinessSummary = runtimeReadinessBlocked
      ? runtimeReadinessMissing
        ? 'Runtime signal unavailable'
        : runtimeReadinessPending
        ? 'Checking local runtime…'
        : runtimeReadiness?.status || 'Runtime is not ready'
      : 'Ready';

    return (
      <div className="space-y-4">
        <div>
          <h3 className="text-sm font-semibold text-gray-900">{title}</h3>
          <p className="mt-1 text-sm text-gray-600">{description}</p>
        </div>

        <div
          className={`rounded border px-3 py-2 text-sm ${
            readinessBlocked ? 'border-amber-200 bg-amber-50 text-amber-900' : 'border-emerald-200 bg-emerald-50 text-emerald-900'
          }`}
        >
          <div className="font-semibold">
            Readiness: {readinessBlocked ? `Blocked (${readinessSummary})` : 'Ready'}
          </div>
          {state.readiness.blockers.length > 0 ? (
            <ul className="mt-1 list-disc space-y-0.5 pl-5 text-xs">
              {state.readiness.blockers.map((blocker) => (
                <li key={blocker}>{blocker}</li>
              ))}
            </ul>
          ) : null}
          {runtimeReadinessBlocked && runtimeReadiness && runtimeDetail ? (
            <p className="mt-1 text-xs">{runtimeDetail}</p>
          ) : null}
          {state.readiness.warnings.length > 0 ? (
            <ul className="mt-1 list-disc space-y-0.5 pl-5 text-xs text-amber-800">
              {state.readiness.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          ) : null}
        </div>

        <div className="rounded border border-gray-200 bg-gray-50 px-3 py-2 text-xs text-gray-700">
          Provider is fixed for this wizard step: <span className="font-semibold">{getServiceProviderDisplayName(localProviderId)}</span>
        </div>

        {manager}

        <ProviderFieldsSection
          provider={localProvider}
          draft={localDraft}
          fieldErrors={fieldErrors}
          onPatch={(patch) => draft.setDraftForProvider(localProviderId, patch)}
          onClearFieldError={clearFieldError}
        />

        {localProvider.runtimeDependencies.length > 0 ? (
          <div className="space-y-2 border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Operational Dependencies</div>
            {localProvider.runtimeDependencies.map((dependency) => (
              <OperationalDependencyRow
                key={dependency.key}
                keyName={dependency.key}
                hasValue={dependency.hasValue}
                currentValue={dependency.currentValue}
              />
            ))}
          </div>
        ) : null}

        {behaviorContent}

        {error ? <p className="text-xs text-red-700">{error}</p> : null}
      </div>
    );
  }
);
