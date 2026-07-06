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
              renderCloudSynthesisBehavior(selectedProvider.providerId)
            ) : (
              <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
                <li>
                  Local TTS strips SSML to plain text before calling <span className="font-mono">POST /tts/synthesize</span>.
                </li>
                <li>
                  Use <span className="font-mono">SpeechSynthesis:TimeoutSeconds</span> for the local HTTP client. The{' '}
                  <span className="font-medium">Voice</span> field below follows the loaded model (reference preset, built-in
                  speaker, optional reference clip, or voice-design text).
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

function renderCloudSynthesisBehavior(providerId: string) {
  switch (providerId) {
    case 'SpeechSynthesis.AzureSpeech.Ssml':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            Microsoft Foundry Speech Services sends SSML directly to Speech (<span className="font-mono">SpeakSsmlAsync</span>). Region and API key are
            required; optional endpoint override is supported.
          </li>
          <li>
            The Microsoft Foundry Speech Services timeout field is stored for synthesis; verify runtime enforcement matches your deployment
            expectations.
          </li>
        </ul>
      );
    case 'SpeechSynthesis.Google.TextToSpeech':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            Google Gemini TTS strips SSML to plain text and calls <span className="font-mono">generateContent</span> with
            audio output enabled.
          </li>
          <li>
            Both <span className="font-mono">TTS Model ID</span> and <span className="font-mono">Voice Name</span> are required.
            Voice selection is stored on the service mode preset, not inferred from the model id.
          </li>
        </ul>
      );
    case 'SpeechSynthesis.HuggingFace.Inference':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            Hugging Face TTS strips SSML to plain text and posts it to the selected <span className="font-mono">TTS Model ID</span>.
          </li>
          <li>
            The shared <span className="font-mono">HuggingFace:Token</span> is required, and the selected model id is used as the
            direct route target.
          </li>
        </ul>
      );
    case 'SpeechSynthesis.OpenRouter.Tts':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            OpenRouter TTS strips SSML to plain text and sends it to <span className="font-mono">/tts</span> using the selected{' '}
            <span className="font-mono">TTS Model ID</span>.
          </li>
          <li>
            The route uses the configured <span className="font-mono">BaseUrl</span> and API key from the shared OpenRouter
            connection section.
          </li>
        </ul>
      );
    case 'SpeechSynthesis.OpenAI.Tts':
      return (
        <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
          <li>
            OpenAI TTS strips SSML to plain text and posts it to <span className="font-mono">/audio/speech</span> using the
            selected <span className="font-mono">TTS Model ID</span> and <span className="font-mono">Voice Name</span>.
          </li>
          <li>
            Both model and voice are required. Available voices include{' '}
            <span className="font-mono">alloy</span>, <span className="font-mono">echo</span>,{' '}
            <span className="font-mono">fable</span>, <span className="font-mono">nova</span>,{' '}
            <span className="font-mono">onyx</span>, <span className="font-mono">shimmer</span>, and others.
          </li>
        </ul>
      );
    default:
      return (
        <p className="text-sm text-gray-700">
          Cloud synthesis uses the selected provider connection and any service-mode fields shown above.
        </p>
      );
  }
}
