import { useEffect, useState } from 'react';
import { api } from '../../../../services/api';
import type { SettingsRuntimeDependencyDto } from '../../../../types/settings';
import { LOCAL_AI_INFRASTRUCTURE_KEYS, SECRET_MASK } from '../constants';
import type { LocalAiPrerequisitesFormState } from '../types';

interface LocalAiPrerequisitesStepProps {
  value: LocalAiPrerequisitesFormState;
  errors: Partial<Record<'huggingFaceToken', string>>;
  onChange: (patch: Partial<LocalAiPrerequisitesFormState>) => void;
}

function InfraStatusBadge({ configured }: { configured: boolean }) {
  return (
    <span
      className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${
        configured ? 'bg-emerald-100 text-emerald-800' : 'bg-amber-100 text-amber-800'
      }`}
    >
      {configured ? 'Configured' : 'Not configured'}
    </span>
  );
}

const KEY_LABELS: Record<string, string> = {
  'LlamaCpp:BaseUrl': 'Llama Runtime (LlamaCpp:BaseUrl)',
  'LocalServiceHosts:EmbeddingsBaseUrl': 'Embeddings Service',
  'LocalServiceHosts:ImageGenerationBaseUrl': 'Image Generation Service',
  'LocalServiceHosts:SpeechTranscriptionBaseUrl': 'Speech Transcription Service',
  'LocalServiceHosts:SpeechSynthesisBaseUrl': 'Speech Synthesis Service',
  'LocalServiceHosts:MediaBaseUrl': 'Media Service',
  'LocalServiceHosts:DocumentIntelligenceBaseUrl': 'Document Intelligence Service',
};

export function LocalAiPrerequisitesStep({ value, errors, onChange }: LocalAiPrerequisitesStepProps) {
  const [infraDeps, setInfraDeps] = useState<SettingsRuntimeDependencyDto[]>([]);
  const [infraLoading, setInfraLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setInfraLoading(true);
    void api.settings.infrastructure.listDependencies().then((deps) => {
      if (!cancelled) {
        setInfraDeps(deps);
        setInfraLoading(false);
      }
    }).catch(() => {
      if (!cancelled) {
        setInfraLoading(false);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const relevantDeps = LOCAL_AI_INFRASTRUCTURE_KEYS.map((key) => {
    const dep = infraDeps.find((d) => d.key === key);
    return {
      key,
      label: KEY_LABELS[key] ?? key,
      configured: Boolean(dep?.currentValue),
    };
  });

  const llamaConfigured = relevantDeps.find((d) => d.key === 'LlamaCpp:BaseUrl')?.configured ?? false;

  return (
    <div className="space-y-5">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Local AI Prerequisites</h3>
        <p className="mt-1 text-sm text-gray-600">
          Local AI requires container-level runtime services to be running and configured. The infrastructure keys
          below are set at the container/environment level and cannot be changed here. Use the{' '}
          <span className="font-medium">Infrastructure</span> tab in Settings to verify connectivity.
        </p>
      </div>

      {!llamaConfigured && !infraLoading ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          <span className="font-semibold">Llama runtime not configured.</span> Chat model downloads and loading
          require <span className="font-mono">LlamaCpp:BaseUrl</span> to be set in the container environment.
          You can still configure local services below, but chat will not function until the runtime is available.
        </div>
      ) : null}

      <div className="space-y-2">
        <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">HuggingFace Token</div>
        <p className="text-xs text-gray-500">
          Required for downloading models from Hugging Face. The token is stored securely in the{' '}
          <span className="font-medium">Connections</span> section.
        </p>
        <input
          type="password"
          value={value.huggingFaceToken}
          placeholder={value.huggingFaceTokenHasStoredValue ? SECRET_MASK : 'hf_...'}
          onChange={(event) => onChange({ huggingFaceToken: event.target.value })}
          className={`w-full rounded border px-3 py-2 text-sm text-gray-900 focus:outline-none focus:ring-1 ${
            errors.huggingFaceToken
              ? 'border-red-400 focus:border-red-400 focus:ring-red-400'
              : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
          }`}
          autoComplete="off"
          spellCheck={false}
        />
        {value.huggingFaceTokenHasStoredValue && !value.huggingFaceToken ? (
          <p className="text-xs text-gray-500">A token is already stored. Enter a new value to replace it.</p>
        ) : null}
        {errors.huggingFaceToken ? (
          <p className="text-xs text-red-600">{errors.huggingFaceToken}</p>
        ) : null}
      </div>

      <div className="space-y-2">
        <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">
          Infrastructure Status
          {infraLoading ? <span className="ml-2 font-normal normal-case text-gray-400">Loading…</span> : null}
        </div>
        <p className="text-xs text-gray-500">
          These keys are configured in the container environment. Unconfigured services will not be available
          until the container is restarted with the required environment variables.
        </p>
        <div className="divide-y divide-gray-100 rounded border border-gray-200">
          {relevantDeps.map((dep) => (
            <div key={dep.key} className="flex items-center justify-between gap-3 px-3 py-2">
              <div>
                <div className="text-sm text-gray-900">{dep.label}</div>
                <div className="font-mono text-xs text-gray-500">{dep.key}</div>
              </div>
              {infraLoading ? (
                <span className="text-xs text-gray-400">…</span>
              ) : (
                <InfraStatusBadge configured={dep.configured} />
              )}
            </div>
          ))}
        </div>
      </div>

      <div className="rounded border border-blue-100 bg-blue-50 px-3 py-2 text-xs text-blue-800">
        You can proceed to model setup even if services are not yet configured. Service settings can be
        adjusted at any time from <span className="font-medium">Settings → Services</span>.
      </div>
    </div>
  );
}
