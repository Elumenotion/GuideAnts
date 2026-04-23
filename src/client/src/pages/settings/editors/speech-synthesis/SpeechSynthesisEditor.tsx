import { FaSave, FaSpinner } from 'react-icons/fa';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { OperationalDependencyRow } from '../../components/shared/OperationalDependencyRow';
import { ProviderFieldsSection } from '../../components/shared/ProviderFieldsSection';
import { ProviderSelector } from '../../components/shared/ProviderSelector';
import { ServiceEditorShell } from '../../components/shared/ServiceEditorShell';
import { useServiceEditorController } from '../../state/useServiceEditorController';
import { TtsModelManager } from './TtsModelManager';

export function SpeechSynthesisEditor() {
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
  } = useServiceEditorController('SpeechSynthesis');

  if (loading) {
    return <div className="text-sm text-gray-600">Loading Speech Synthesis settings…</div>;
  }

  if (!state || !selectedProvider) {
    return <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error ?? 'Not found.'}</div>;
  }

  const isLocal = selectedProvider.providerKind !== 'Cloud';

  return (
    <ServiceEditorShell
      serviceName="Speech Synthesis"
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
        <div className="space-y-6">
          {isLocal ? <TtsModelManager enabled={isLocal} /> : null}

          <ProviderFieldsSection
            provider={selectedProvider}
            draft={draft.activeDraft}
            fieldErrors={fieldErrors}
            onPatch={(patch) => draft.patchActiveDraft(patch)}
            onClearFieldError={clearFieldError}
          />

          <div className="space-y-2 border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Synthesis behavior</div>
            {!isLocal ? (
              <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
                <li>
                  Azure path sends SSML directly to Azure Speech (<span className="font-mono">SpeakSsmlAsync</span>). Region
                  and API key are required; optional endpoint override is supported.
                </li>
                <li>
                  The Azure Speech timeout field is stored for synthesis; verify runtime enforcement matches your deployment
                  expectations.
                </li>
              </ul>
            ) : (
              <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
                <li>
                  Local TTS strips SSML to plain text before calling <span className="font-mono">POST /tts/synthesize</span>.
                </li>
                <li>
                  Use <span className="font-mono">SpeechSynthesis:TimeoutSeconds</span> for the local HTTP client. Use the{' '}
                  <span className="font-medium">Local TTS model</span> section above to load a model and tokenizer in-app.
                </li>
              </ul>
            )}
          </div>

          {selectedProvider.runtimeDependencies.length > 0 ? (
            <div className="space-y-2 border-t border-gray-100 pt-4">
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
        </div>
      }
      actions={
        <>
          {error ? <span className="mr-3 text-xs text-red-700">{error}</span> : null}
          <TextActionButton
            tone="primary"
            icon={saving ? <FaSpinner className="animate-spin" /> : <FaSave />}
            disabled={saving}
            onClick={() => void save()}
            title="Save and activate provider."
          >
            Save
          </TextActionButton>
        </>
      }
    />
  );
}
