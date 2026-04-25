import { useState } from 'react';
import { FaRedo, FaSpinner } from 'react-icons/fa';
import { api } from '../../../../services/api';
import type { ProviderEditorStateDto } from '../../../../types/settings';
import { TextActionButton } from '../../components/shared/ActionButtons';
import { ServiceEditorBase } from '../common/ServiceEditorBase';
import { EmbRuntimeManager } from './EmbRuntimeManager';

export function EmbeddingsEditor() {
  const [rebuilding, setRebuilding] = useState(false);
  const [rebuildMessage, setRebuildMessage] = useState<string | null>(null);

  const triggerRebuild = async (): Promise<void> => {
    const confirmed = window.confirm(
      'Delete and regenerate vectors from markdown shadows? This can take time and cannot be undone.'
    );
    if (!confirmed) {
      return;
    }

    setRebuilding(true);
    setRebuildMessage(null);
    try {
      const response = await api.settings.rebuildEmbeddings();
      setRebuildMessage(`Rebuild queued: job ${response.jobId} (${response.status}).`);
    } catch (error: unknown) {
      const err = error as { status?: number; body?: { jobId?: string; status?: string } };
      const body = err?.body;
      if (err?.status === 409) {
        setRebuildMessage(
          body?.jobId ? `Rebuild already running: job ${body.jobId} (${body.status}).` : 'Rebuild already running.'
        );
      } else {
        setRebuildMessage(error instanceof Error ? error.message : 'Failed to queue rebuild.');
      }
    } finally {
      setRebuilding(false);
    }
  };

  return (
    <ServiceEditorBase
      serviceId="Embeddings"
      title="Embeddings"
      providerExtraTop={(provider: ProviderEditorStateDto) =>
        provider.providerKind !== 'Cloud' ? <EmbRuntimeManager enabled /> : null
      }
      providerExtra={(provider: ProviderEditorStateDto) => (
        <div className="space-y-4 border-t border-gray-100 pt-4">
          <div>
            <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Runtime behavior</div>
            {provider.providerKind === 'Cloud' ? (
              renderCloudEmbeddingsBehavior(provider)
            ) : (
              <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
                <li>
                  <span className="font-medium">Local pacing:</span> Minimum interval between local embedding calls is enforced by{' '}
                  <code className="rounded bg-gray-100 px-1">LocalMinIntervalMs</code>.
                </li>
                <li>
                  <span className="font-medium">Single in-flight:</span> The local embedding client serializes concurrent requests so pacing remains predictable.
                </li>
                <li>
                  <span className="font-medium">Dimensional adaptation:</span> When the local model outputs 1024-d vectors, storage adapts to the configured vector width (e.g. 1536) as provider behavior — not a generic cross-provider knob.
                </li>
              </ul>
            )}
          </div>
        </div>
      )}
      extraActions={
        <div className="flex items-center gap-2">
          {rebuildMessage ? <span className="text-xs text-gray-700">{rebuildMessage}</span> : null}
          <TextActionButton
            tone="danger"
            icon={rebuilding ? <FaSpinner className="animate-spin" /> : <FaRedo />}
            disabled={rebuilding}
            onClick={() => void triggerRebuild()}
            title="Delete and regenerate vectors from markdown shadows."
          >
            Rebuild vectors
          </TextActionButton>
        </div>
      }
    />
  );
}

function renderCloudEmbeddingsBehavior(provider: ProviderEditorStateDto) {
  switch (provider.providerId) {
    case 'Embeddings.AzureOpenAI.Embedding':
      return (
        <p className="text-sm text-gray-700">
          Azure OpenAI embedding requests use the configured endpoint and deployment. Credentials are stored securely and are
          not echoed back in API responses.
        </p>
      );
    case 'Embeddings.Google.Embedding':
      return (
        <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
          <li>
            Google embeddings use the shared Gemini API connection (<span className="font-mono">ApiKey</span>) together
            with the selected{' '}
            <span className="font-mono">Embedding Model ID</span>.
          </li>
          <li>
            The model id is stored on the active routing mode, so changing providers does not duplicate the Google connection.
          </li>
        </ul>
      );
    case 'Embeddings.HuggingFace.Inference':
      return (
        <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
          <li>
            Hugging Face embeddings call the selected <span className="font-mono">Embedding Model ID</span> with the shared{' '}
            <span className="font-mono">HuggingFace:Token</span>.
          </li>
          <li>
            Use <span className="font-mono">HuggingFace:EmbeddingAllowedModels</span> when you want to pin the route to an
            approved model set.
          </li>
        </ul>
      );
    case 'Embeddings.OpenRouter.Embeddings':
      return (
        <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
          <li>
            OpenRouter embeddings use the shared connection settings plus the selected{' '}
            <span className="font-mono">Embedding Model ID</span>.
          </li>
          <li>
            Use <span className="font-mono">OpenRouter:EmbeddingAllowedModels</span> to constrain which embedding models are
            allowed for this route.
          </li>
        </ul>
      );
    default:
      return (
        <p className="text-sm text-gray-700">
          Cloud embeddings use the selected provider connection and any service-mode fields shown above.
        </p>
      );
  }
}
