export type NotebookToolbarServiceKind = 'image' | 'tts' | 'asr';

export interface NotebookToolbarModelOptionDto {
  modelId: string;
  displayName: string;
  provider: string;
  isActive: boolean;
}

export interface NotebookToolbarProviderOptionDto {
  providerId: string;
  displayName: string;
  providerKind: string;
  canActivate: boolean;
  blockers: string[];
  providerSection?: string | null;
  modelId?: string | null;
}

export interface NotebookToolbarSelectionDto {
  resourceId: string | null;
  summary: string | null;
}

export interface NotebookToolbarLocalModelOptionDto {
  modelRef: string;
  displayLabel: string;
  isComplete: boolean;
  isActive: boolean;
}

export interface NotebookToolbarServiceDto {
  serviceId: string;
  displayName: string;
  kind: NotebookToolbarServiceKind;
  status: string;
  summary: string;
  activeProviderId: string;
  activeProviderLabel: string;
  supportsLocalRuntimePower: boolean;
  localRuntimeOn: boolean;
  providerOptions: NotebookToolbarProviderOptionDto[];
  selection: NotebookToolbarSelectionDto | null;
  blockers: string[];
  localModelOptions: NotebookToolbarLocalModelOptionDto[];
  inProgressOperationId: string | null;
  inProgressState: string | null;
}

export interface NotebookToolbarChatDto {
  status: string;
  summary: string;
  conversationId: string | null;
  selectedAssistantName: string | null;
  effectiveModelId: string | null;
  effectiveModelDisplayName: string | null;
  effectiveProvider: string | null;
  overrideAllChatModels: boolean;
  supportsLocalRuntimePower: boolean;
  localRuntimeOn: boolean;
  modelOptions: NotebookToolbarModelOptionDto[];
  blockers: string[];
  inProgressOperationId: string | null;
  inProgressState: string | null;
}

export interface NotebookHeaderToolbarDto {
  chat: NotebookToolbarChatDto;
  services: NotebookToolbarServiceDto[];
  generatedUtc: string;
}

export interface ModelLoadOperationDto {
  operationId: string;
  state: string;
  startedAt: string;
  completedAt?: string | null;
  targetAssistantId?: string | null;
  errorDetails?: string | null;
}
