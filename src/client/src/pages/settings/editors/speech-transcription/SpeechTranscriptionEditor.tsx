import { FaSave, FaSpinner } from 'react-icons/fa';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { OperationalDependencyRow } from '../../components/shared/OperationalDependencyRow';
import { ProviderFieldsSection } from '../../components/shared/ProviderFieldsSection';
import { ProviderSelector } from '../../components/shared/ProviderSelector';
import { ServiceEditorShell } from '../../components/shared/ServiceEditorShell';
import { useServiceEditorController } from '../../state/useServiceEditorController';
import { AsrModelManager } from './AsrModelManager';

export function SpeechTranscriptionEditor() {
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
  } = useServiceEditorController('SpeechTranscription');

  if (loading) {
    return <div className="text-sm text-gray-600">Loading Speech Transcription settings…</div>;
  }

  if (!state || !selectedProvider) {
    return <div className="rounded border border-red-200 bg-red-50 p-3 text-sm text-red-700">{error ?? 'Not found.'}</div>;
  }

  const isLocal = selectedProvider.providerKind !== 'Cloud';

  return (
    <ServiceEditorShell
      serviceName="Speech Transcription"
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
          {isLocal ? <AsrModelManager enabled={isLocal} /> : null}

          <ProviderFieldsSection
            provider={selectedProvider}
            draft={draft.activeDraft}
            fieldErrors={fieldErrors}
            onPatch={(patch) => draft.patchActiveDraft(patch)}
            onClearFieldError={clearFieldError}
          />

          <div className="space-y-2 border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Transcription behavior</div>
            {!isLocal ? (
              renderCloudTranscriptionBehavior(selectedProvider.providerId)
            ) : (
              <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
                <li>
                  Local ASR uses <span className="font-mono">SpeechTranscription:TimeoutSeconds</span> for HTTP calls to the
                  local service.
                </li>
                <li>
                  Runtime tunables (<span className="font-mono">GA_ASR_*</span>) are container-owned; use the{' '}
                  <span className="font-medium">Local ASR model</span> section above for in-app model lifecycle.
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
                  hasValue={dependency.hasValue}
                  currentValue={dependency.currentValue}
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

function renderCloudTranscriptionBehavior(providerId: string) {
  switch (providerId) {
    case 'SpeechTranscription.AzureSpeech.Batch':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            Cloud batch transcription uses <span className="font-mono">AzureSpeechService:Endpoint</span>,{' '}
            <span className="font-mono">ApiKey</span>, and the Microsoft Foundry Speech Services timeout stored for this path.
          </li>
          <li>Diarization and structured output follow Microsoft Foundry Speech Services behavior when enabled in your workflows.</li>
        </ul>
      );
    case 'SpeechTranscription.Google.SpeechToText':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            Google Gemini transcription sends the uploaded audio to the selected{' '}
            <span className="font-mono">Transcription Model ID</span> through the shared Gemini API connection.
          </li>
          <li>
            The route submits an audio part plus a transcription prompt over <span className="font-mono">generateContent</span>,
            so the selected model must support audio input.
          </li>
        </ul>
      );
    case 'SpeechTranscription.HuggingFace.Inference':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            Hugging Face ASR posts the uploaded audio to the selected <span className="font-mono">ASR Model ID</span> using
            the shared <span className="font-mono">HuggingFace:Token</span>.
          </li>
          <li>
            Use <span className="font-mono">Allowed Models</span> when you want to lock the route to an approved
            model set.
          </li>
        </ul>
      );
    case 'SpeechTranscription.OpenRouter.Audio':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            OpenRouter transcription sends the audio payload to the configured <span className="font-mono">BaseUrl</span> using
            the selected <span className="font-mono">Transcription Model ID</span>.
          </li>
          <li>
            Audio format support and request size are constrained by the upstream adapter; the size cap comes from this
            service mode's <span className="font-mono">Max Audio Bytes</span>.
          </li>
        </ul>
      );
    case 'SpeechTranscription.OpenAI.Audio':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            OpenAI transcription posts audio to <span className="font-mono">/audio/transcriptions</span> using the selected{' '}
            <span className="font-mono">Transcription Model ID</span> (e.g. <span className="font-mono">whisper-1</span>).
          </li>
          <li>
            The shared <span className="font-mono">OpenAI:ApiKey</span> and optional{' '}
            <span className="font-mono">OpenAI:Endpoint</span> are used for authentication and routing.
          </li>
        </ul>
      );
    default:
      return (
        <p className="text-sm text-gray-700">
          Cloud transcription uses the selected provider connection and any service-mode fields shown above.
        </p>
      );
  }
}
