import type { AddModelRequest, LlamaRuntimeInventoryItemDto, ModelDownloadOperationDto } from '../../types/settings';

export type LocalModelOnboardingSource = 'huggingface' | 'existingAlias';

export interface LocalModelOnboardingDraft {
  installSource: LocalModelOnboardingSource;
  runtimeProfileId: string;
  routerModelId: string;
  huggingFaceRepository: string;
  huggingFaceQuantIncludePattern: string;
  huggingFaceMmprojIncludePattern: string;
  huggingFaceTargetDirectory: string;
  existingAliasRouterModelId: string;
  routerContextSize: string;
  routerCacheRamMib: string;
  catalogModelId: string;
  catalogDisplayName: string;
  catalogDescription?: string;
  catalogDisplayOrder?: string;
  catalogIsActive?: boolean;
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
