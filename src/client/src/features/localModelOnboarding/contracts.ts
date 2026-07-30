import type { AddModelRequest, LlamaRuntimeInventoryItemDto, ModelDownloadOperationDto } from '../../types/settings';
import type { PresetKeyValue } from './routerPreset';

export type LocalModelOnboardingSource = 'huggingface' | 'existingAlias' | 'curated';

export interface LocalModelOnboardingDraft {
  installSource: LocalModelOnboardingSource;
  routerModelId: string;
  huggingFaceRepository: string;
  huggingFaceResolvedRevision: string;
  huggingFaceArtifactGroupId: string;
  huggingFaceModelFiles: string[];
  huggingFaceMmprojFiles: string[];
  huggingFaceTargetDirectory: string;
  huggingFaceRouterPresetRows: PresetKeyValue[];
  huggingFacePresetMode: 'replace' | 'merge';
  existingAliasRouterModelId: string;
  catalogModelId: string;
  catalogDisplayName: string;
  catalogDescription?: string;
  catalogDisplayOrder?: string;
  catalogIsActive?: boolean;
  samplingParametersJson: string;
  reasoningChoicesJson: string;
  thinkingControlJson: string;
  requestFieldsWhenToolsPresentJson: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern: string;
}

export type LocalModelOnboardingStatus =
  | 'submitted'
  | 'queued'
  | 'resolvingFiles'
  | 'downloading'
  | 'registeringAlias'
  | 'completed'
  | 'error';

export type LocalModelOnboardingOperation = Pick<ModelDownloadOperationDto, 'operationId' | 'status' | 'progress' | 'errorMessage' | 'error'>;

export type LocalModelAttachableAlias = LlamaRuntimeInventoryItemDto;

export type LocalModelOnboardingRequest = AddModelRequest;
