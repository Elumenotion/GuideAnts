import { forwardRef, useState } from 'react';
import type { LocalDownloadOperationState, LocalRuntimeReadinessState } from '../../../../pages/settings/editors/common/localOperationPolling';
import { EmbRuntimeManager } from '../../../../pages/settings/editors/embeddings/EmbRuntimeManager';
import { LOCAL_AI_SERVICE_PROVIDER_IDS } from '../constants';
import { LocalAiServiceStepBase, type LocalAiServiceStepHandle } from './LocalAiServiceStepBase';

interface LocalAiEmbeddingsStepProps {
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
}

export const LocalAiEmbeddingsStep = forwardRef<LocalAiServiceStepHandle, LocalAiEmbeddingsStepProps>(
  function LocalAiEmbeddingsStep({ onDownloadOperationChange }, ref) {
    const [runtimeReadiness, setRuntimeReadiness] = useState<LocalRuntimeReadinessState | null>(null);
    return (
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="Embeddings"
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
        requireRuntimeReadiness
        title="Local Embeddings"
        description="Configure local embedding routing fields and manage embedding model downloads for the embeddings service."
        runtimeReadiness={runtimeReadiness}
        manager={(
          <EmbRuntimeManager
            enabled
            onDownloadOperationChange={onDownloadOperationChange}
            onRuntimeReadinessChange={setRuntimeReadiness}
          />
        )}
        behaviorContent={(
          <div className="space-y-4 border-t border-gray-100 pt-4">
            <div>
              <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Runtime behavior</div>
              <ul className="list-disc space-y-2 pl-5 text-sm text-gray-700">
                <li>
                  <span className="font-medium">Local pacing:</span> minimum interval between local embedding calls is enforced by{' '}
                  <code className="rounded bg-gray-100 px-1">LocalMinIntervalMs</code>.
                </li>
                <li>
                  <span className="font-medium">Single in-flight:</span> the local embedding client serializes concurrent requests so pacing remains predictable.
                </li>
                <li>
                  <span className="font-medium">Dimensional adaptation:</span> local outputs adapt to configured vector width as provider behavior.
                </li>
              </ul>
            </div>
          </div>
        )}
      />
    );
  }
);
