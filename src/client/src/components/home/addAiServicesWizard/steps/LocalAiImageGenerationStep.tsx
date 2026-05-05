import { forwardRef, useState } from 'react';
import type { LocalDownloadOperationState, LocalRuntimeReadinessState } from '../../../../pages/settings/editors/common/localOperationPolling';
import { ImageBundleManager } from '../../../../pages/settings/editors/image-generation/ImageBundleManager';
import { localSdAllowedSizes } from '../../../../pages/settings/editors/image-generation/imageGenerationUi';
import { LOCAL_AI_SERVICE_PROVIDER_IDS } from '../constants';
import { LocalAiServiceStepBase, type LocalAiServiceStepHandle } from './LocalAiServiceStepBase';

interface LocalAiImageGenerationStepProps {
  onDownloadOperationChange?: (state: LocalDownloadOperationState | null) => void;
}

export const LocalAiImageGenerationStep = forwardRef<LocalAiServiceStepHandle, LocalAiImageGenerationStepProps>(
  function LocalAiImageGenerationStep({ onDownloadOperationChange }, ref) {
    const [runtimeReadiness, setRuntimeReadiness] = useState<LocalRuntimeReadinessState | null>(null);
    return (
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="ImageGeneration"
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration}
        requireRuntimeReadiness
        title="Local Image Generation"
        description="Configure local Stable Diffusion routing fields and manage image bundles for the generation service."
        runtimeReadiness={runtimeReadiness}
        manager={(
          <ImageBundleManager
            enabled
            onDownloadOperationChange={onDownloadOperationChange}
            onRuntimeReadinessChange={setRuntimeReadiness}
          />
        )}
        behaviorContent={(
          <div className="space-y-2 border-t border-gray-100 pt-4">
            <div className="text-xs font-semibold uppercase tracking-wide text-gray-600">Generation profile</div>
            <p className="text-sm text-gray-700">
              Local Stable Diffusion uses flux-style sizes: {localSdAllowedSizes.join(', ')}.
            </p>
            <p className="text-xs text-gray-600">
              Default output format is saved above and applied to local txt2img/img2img calls.
            </p>
          </div>
        )}
      />
    );
  }
);
