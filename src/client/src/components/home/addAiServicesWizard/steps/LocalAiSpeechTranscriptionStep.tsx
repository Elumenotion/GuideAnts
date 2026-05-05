import { forwardRef, useState } from 'react';
import { AsrModelManager } from '../../../../pages/settings/editors/speech-transcription/AsrModelManager';
import type { LocalDownloadOperationState, LocalRuntimeReadinessState } from '../../../../pages/settings/editors/common/localOperationPolling';
import { LOCAL_AI_SERVICE_PROVIDER_IDS } from '../constants';
import { LocalAiServiceStepBase, type LocalAiServiceStepHandle } from './LocalAiServiceStepBase';

interface LocalAiSpeechTranscriptionStepProps {
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
}

export const LocalAiSpeechTranscriptionStep = forwardRef<LocalAiServiceStepHandle, LocalAiSpeechTranscriptionStepProps>(
  function LocalAiSpeechTranscriptionStep({ onDownloadOperationChange }, ref) {
    const [runtimeReadiness, setRuntimeReadiness] = useState<LocalRuntimeReadinessState | null>(null);
    return (
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="SpeechTranscription"
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription}
        requireRuntimeReadiness
        title="Local Speech Transcription"
        description="Configure local ASR routing fields and manage ASR model downloads for the transcription service."
        runtimeReadiness={runtimeReadiness}
        manager={(
          <AsrModelManager
            enabled
            onDownloadOperationChange={onDownloadOperationChange}
            onRuntimeReadinessChange={setRuntimeReadiness}
          />
        )}
        behaviorContent={(
          <div className="space-y-2 border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Transcription behavior</div>
            <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
              <li>
                Local ASR uses <span className="font-mono">SpeechTranscription:TimeoutSeconds</span> for HTTP calls to the local service.
              </li>
              <li>
                Runtime tunables (<span className="font-mono">GA_ASR_*</span>) are container-owned; use the <span className="font-medium">Local ASR model</span> manager above for in-app model lifecycle.
              </li>
            </ul>
          </div>
        )}
      />
    );
  }
);
