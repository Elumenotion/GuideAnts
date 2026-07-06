import { forwardRef, useState } from 'react';
import type { LocalDownloadOperationState, LocalRuntimeReadinessState } from '../../../../pages/settings/editors/common/localOperationPolling';
import { TtsModelManager } from '../../../../pages/settings/editors/speech-synthesis/TtsModelManager';
import { LOCAL_AI_SERVICE_PROVIDER_IDS } from '../constants';
import { LocalAiServiceStepBase, type LocalAiServiceStepHandle } from './LocalAiServiceStepBase';

interface LocalAiSpeechSynthesisStepProps {
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
}

export const LocalAiSpeechSynthesisStep = forwardRef<LocalAiServiceStepHandle, LocalAiSpeechSynthesisStepProps>(
  function LocalAiSpeechSynthesisStep({ onDownloadOperationChange }, ref) {
    const [runtimeReadiness, setRuntimeReadiness] = useState<LocalRuntimeReadinessState | null>(null);
    return (
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="SpeechSynthesis"
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis}
        requireRuntimeReadiness
        title="Local Speech Synthesis"
        description="Configure local TTS routing fields and manage TTS model downloads for the synthesis service."
        runtimeReadiness={runtimeReadiness}
        manager={(
          <TtsModelManager
            enabled
            onDownloadOperationChange={onDownloadOperationChange}
            onRuntimeReadinessChange={setRuntimeReadiness}
          />
        )}
        behaviorContent={(
          <div className="space-y-2 border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Synthesis behavior</div>
            <ul className="list-disc space-y-1 pl-5 text-sm text-gray-700">
              <li>
                Local TTS strips SSML to plain text before calling <span className="font-mono">POST /tts/synthesize</span>.
              </li>
              <li>
                Use <span className="font-mono">SpeechSynthesis:TimeoutSeconds</span> for the local HTTP client. Use the <span className="font-medium">Reference voice</span> field below to pick a voice from the baked pack.
              </li>
            </ul>
          </div>
        )}
      />
    );
  }
);
