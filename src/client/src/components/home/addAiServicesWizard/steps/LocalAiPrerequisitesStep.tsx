import { useEffect, useMemo, useState } from 'react';
import { api } from '../../../../services/api';
import { SECRET_MASK } from '../constants';
import type { LocalAiPrerequisitesFormState } from '../types';

interface LocalAiPrerequisitesStepProps {
  value: LocalAiPrerequisitesFormState;
  errors: Partial<Record<'huggingFaceToken', string>>;
  onChange: (patch: Partial<LocalAiPrerequisitesFormState>) => void;
  localChatModelCount: number;
}

interface ModelStatusBadgeProps {
  configured: boolean;
}

interface ServiceModelStatus {
  configured: boolean;
  count: number;
  detail?: string;
}

type ServiceModelStatusMap = Record<string, ServiceModelStatus>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function getInstalledCount(payload: unknown): number {
  if (!isRecord(payload)) {
    return 0;
  }

  const items = payload.items;
  if (!Array.isArray(items)) {
    return 0;
  }

  return items.length;
}

function ModelStatusBadge({ configured }: ModelStatusBadgeProps) {
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

const LOCAL_MODEL_SERVICES: Array<{ serviceId: string; label: string }> = [
  { serviceId: 'SpeechTranscription', label: 'Speech Transcription Service' },
  { serviceId: 'SpeechSynthesis', label: 'Speech Synthesis Service' },
  { serviceId: 'ImageGeneration', label: 'Image Generation Service' },
  { serviceId: 'Embeddings', label: 'Embeddings Service' },
];

const SERVICE_MODEL_STATUS_LABELS: Record<string, string> = {
  SpeechTranscription: 'ASR model installed',
  SpeechSynthesis: 'TTS model installed',
  ImageGeneration: 'Image bundle installed',
  Embeddings: 'Embedding model installed',
};

export function LocalAiPrerequisitesStep({
  value,
  errors,
  onChange,
  localChatModelCount,
}: LocalAiPrerequisitesStepProps) {
  const [serviceModelStatuses, setServiceModelStatuses] = useState<ServiceModelStatusMap>({});
  const [modelStatusLoading, setModelStatusLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;

    const loadServiceModelStatus = async () => {
      setModelStatusLoading(true);
      const entries = await Promise.all(
        LOCAL_MODEL_SERVICES.map(async ({ serviceId }) => {
          try {
            const outcome = await api.settings.localModels.listOutcome(serviceId);
            if (outcome.kind !== 'available') {
              return [
                serviceId,
                { configured: false, count: 0, detail: outcome.message || 'Service unavailable.' },
              ] as const;
            }

            const count = getInstalledCount(outcome.payload);
            return [
              serviceId,
              {
                configured: count > 0,
                count,
                detail: count > 0
                  ? `${count} installed`
                  : SERVICE_MODEL_STATUS_LABELS[serviceId] ?? 'No model installed',
              },
            ] as const;
          } catch (error) {
            const detail = error instanceof Error ? error.message : 'Service unavailable.';
            return [serviceId, { configured: false, count: 0, detail }] as const;
          }
        })
      );

      if (cancelled) {
        return;
      }

      setServiceModelStatuses(Object.fromEntries(entries));
      setModelStatusLoading(false);
    };

    void loadServiceModelStatus().catch(() => {
      if (!cancelled) {
        setModelStatusLoading(false);
      }
    });

    return () => {
      cancelled = true;
    };
  }, []);

  const modelReadinessRows = useMemo(
    () =>
      LOCAL_MODEL_SERVICES.map(({ serviceId, label }) => ({
        serviceId,
        label,
        status: serviceModelStatuses[serviceId] ?? {
          configured: false,
          count: 0,
          detail: 'No model installed',
        },
      })),
    [serviceModelStatuses]
  );
  const llamaConfigured = localChatModelCount > 0;

  return (
    <div className="space-y-5">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Local AI Prerequisites</h3>
        <p className="mt-1 text-sm text-gray-600">
          Local AI readiness in this step is model-based: a service is marked configured only after a model or
          bundle is installed for that service.
        </p>
      </div>

      {!llamaConfigured ? (
        <div className="rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
          <span className="font-semibold">No local chat model configured.</span> Install at least one local
          llama-cpp model on the next step to enable basic local chat.
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
          Model Status
          {modelStatusLoading ? <span className="ml-2 font-normal normal-case text-gray-400">Loading…</span> : null}
        </div>
        <p className="text-xs text-gray-500">
          Configured means the service has at least one installed model/bundle. Infrastructure URL presence is not
          used for these badges.
        </p>
        <div className="divide-y divide-gray-100 rounded border border-gray-200">
          <div className="flex items-center justify-between gap-3 px-3 py-2">
            <div>
              <div className="text-sm text-gray-900">Llama Chat Models</div>
              <div className="text-xs text-gray-500">
                {llamaConfigured ? `${localChatModelCount} installed` : 'No local chat model installed'}
              </div>
            </div>
            <ModelStatusBadge configured={llamaConfigured} />
          </div>
          {modelReadinessRows.map((row) => (
            <div key={row.serviceId} className="flex items-center justify-between gap-3 px-3 py-2">
              <div>
                <div className="text-sm text-gray-900">{row.label}</div>
                <div className="text-xs text-gray-500">{row.status.detail ?? 'No model installed'}</div>
              </div>
              {modelStatusLoading ? (
                <span className="text-xs text-gray-400">…</span>
              ) : (
                <ModelStatusBadge configured={row.status.configured} />
              )}
            </div>
          ))}
          <div className="flex items-center justify-between gap-3 px-3 py-2">
            <div>
              <div className="text-sm text-gray-900">Document Intelligence Service</div>
              <div className="text-xs text-gray-500">No model install required in this wizard.</div>
            </div>
            <span className="inline-block rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-700">
              N/A
            </span>
          </div>
        </div>
      </div>

      <div className="rounded border border-blue-100 bg-blue-50 px-3 py-2 text-xs text-blue-800">
        Continue to <span className="font-medium">Models</span> to install local chat models, then service steps to
        install any ASR/TTS/Image/Embeddings models you need.
      </div>
    </div>
  );
}
