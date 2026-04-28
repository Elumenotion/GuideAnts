import type { ReactNode } from 'react';
import { FaSave, FaSpinner } from 'react-icons/fa';
import type { ProviderEditorStateDto } from '../../../../types/settings';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { OperationalDependencyRow } from '../../components/shared/OperationalDependencyRow';
import { ProviderFieldsSection } from '../../components/shared/ProviderFieldsSection';
import { ProviderSelector } from '../../components/shared/ProviderSelector';
import { ServiceEditorShell } from '../../components/shared/ServiceEditorShell';
import { useServiceEditorController } from '../../state/useServiceEditorController';

/**
 * Shared plumbing for service editors: load/save, draft provider, operative fields, operational dependencies.
 * Service-specific panels pass via `providerExtra` / `serviceSettings`.
 */
interface ServiceEditorBaseProps {
  serviceId: string;
  title: string;
  /** Rendered before operative fields and dependencies (e.g. local runtime controls). */
  providerExtraTop?: ReactNode | ((selectedProvider: ProviderEditorStateDto) => ReactNode);
  /** Rendered after operative fields and operational dependencies (e.g. runtime behavior notes). */
  providerExtra?: ReactNode | ((selectedProvider: ProviderEditorStateDto) => ReactNode);
  serviceSettings?: ReactNode;
  extraActions?: ReactNode;
}

export function ServiceEditorBase({
  serviceId,
  title,
  providerExtraTop,
  providerExtra,
  serviceSettings,
  extraActions,
}: ServiceEditorBaseProps) {
  const {
    state,
    loading,
    error,
    saving,
    fieldErrors,
    draft,
    selectedProvider,
    persistedActiveLabel,
    editingProviderLabel,
    providerOptions,
    save,
    clearFieldError,
  } = useServiceEditorController(serviceId);

  if (loading) {
    return <div className="text-sm text-gray-600">Loading {title} settings…</div>;
  }

  if (!state || !selectedProvider) {
    return <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error ?? 'Not found.'}</div>;
  }

  return (
    <ServiceEditorShell
      serviceName={title}
      activeProviderLabel={persistedActiveLabel}
      editingProviderLabel={editingProviderLabel}
      readinessStatus={state.readiness.status}
      readinessSummary={state.readiness.blockers.length > 0 ? state.readiness.blockers.join(' | ') : 'Ready'}
      providerSelector={
        <div className="space-y-2">
          <ProviderSelector
            value={draft.activeProviderId}
            options={providerOptions}
            onChange={(id) => draft.switchProvider(id)}
          />
          <p className="text-xs text-gray-500">
            Select a provider, adjust settings, then click <span className="font-medium">Save and activate provider</span>.
          </p>
        </div>
      }
      providerSettings={
        <div className="space-y-4">
          {typeof providerExtraTop === 'function' ? providerExtraTop(selectedProvider) : providerExtraTop}
          <ProviderFieldsSection
            provider={selectedProvider}
            draft={draft.activeDraft}
            fieldErrors={fieldErrors}
            onPatch={(patch) => draft.patchActiveDraft(patch)}
            onClearFieldError={clearFieldError}
          />
          {selectedProvider.runtimeDependencies.length > 0 ? (
            <div className="space-y-2 pt-2">
              <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Operational Dependencies</div>
              {selectedProvider.runtimeDependencies.map((dependency) => (
                <OperationalDependencyRow
                  key={dependency.key}
                  keyName={dependency.key}
                  displayName={dependency.displayName}
                  hasValue={dependency.hasValue}
                  currentValue={dependency.currentValue}
                  changeHint={dependency.changeHint}
                />
              ))}
            </div>
          ) : null}
          {typeof providerExtra === 'function' ? providerExtra(selectedProvider) : providerExtra}
        </div>
      }
      serviceSettings={serviceSettings}
      actions={
        <>
          {error ? <span className="mr-3 text-xs text-red-700">{error}</span> : null}
          {extraActions}
          <TextActionButton
            tone="primary"
            icon={saving ? <FaSpinner className="animate-spin" /> : <FaSave />}
            disabled={saving || !selectedProvider.connectionConfigured}
            onClick={() => void save()}
            title={
              !selectedProvider.connectionConfigured
                  ? 'Configure the provider connection first.'
                  : !selectedProvider.hasExplicitMode
                    ? 'Save will create an explicit service mode and activate provider.'
                    : 'Save and activate provider.'
            }
          >
            Save
          </TextActionButton>
        </>
      }
    />
  );
}
