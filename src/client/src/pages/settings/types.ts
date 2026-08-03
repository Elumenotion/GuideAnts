export type SettingsTab =
  | 'overview'
  | 'personalization'
  | 'users'
  | 'telemetry'
  | 'services'
  | 'sandbox'
  | 'connections'
  | 'models-runtime'
  | 'infrastructure';

/**
 * Sub-tabs rendered inside the Models & Runtime workspace. Accepted as a deep-link
 * target when other settings tabs navigate the user here.
 */
export type ModelsRuntimeSubTab = 'catalog' | 'local-llama';

/**
 * Deep-link payload passed to the Models & Runtime workspace.
 * `focusedAlias` is set when a per-row Load/Unload action asks the workspace to
 * land on a specific llama runtime alias.
 */
export interface ModelsRuntimeDeepLink {
  subTab: ModelsRuntimeSubTab;
  focusedAlias?: string;
  focusedModelId?: string;
}

/** Opens Add Model wizard; optional second arg preselects attach-existing-alias flow. */
export type OpenAddModelWizardHandler = (
  providerPreselect?: string,
  attachAliasRouterModelId?: string
) => void;

export type PendingConfirmation =
  | { kind: 'rebuild-embeddings' }
  | { kind: 'delete-model'; modelId: string }
  | { kind: 'unload-llama-router'; routerModelId: string; notebookReferenceCount: number }
  | {
      kind: 'delete-llama-router';
      routerModelId: string;
      catalogModelIds: string[];
      notebookReferenceCount: number;
    }
  | null;

export type AddModelWizardStep = 'provider' | 'catalog' | 'providerConfig' | 'review' | 'progress';

export type AddModelProvider =
  | 'openai-chat'
  | 'openai-responses'
  | 'azure-openai-chat'
  | 'azure-openai-responses'
  | 'anthropic'
  | 'llama-cpp'
  | 'google-gemini-chat'
  | 'hf-inference-chat'
  | 'openrouter-chat';

export interface AddModelWizardState {
  provider: AddModelProvider | '';
  catalogModelId: string;
  catalogDisplayName: string;
  catalogDescription: string;
  catalogDisplayOrder: string;
  catalogIsActive: boolean;
  samplingParametersJson: string;
  reasoningChoicesJson: string;
  thinkingControlJson: string;
  requestFieldsWhenToolsPresentJson: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern: string;
  llamaInstallSource: 'huggingface' | 'existingAlias';
  llamaRouterModelId: string;
  llamaHuggingFaceRepository: string;
  llamaHuggingFaceResolvedRevision: string;
  llamaHuggingFaceArtifactGroupId: string;
  llamaHuggingFaceModelFiles: string[];
  llamaHuggingFaceMmprojFiles: string[];
  llamaHuggingFaceTargetDirectory: string;
  llamaHuggingFaceRouterPresetRows: Array<{ key: string; value: string }>;
  llamaHuggingFacePresetMode: 'replace' | 'merge';
  llamaExistingAliasRouterModelId: string;
}

export interface CatalogEditState {
  modelId: string;
  provider: string;
  displayName: string;
  description: string;
  displayOrder: string;
  isActive: boolean;
  samplingParametersJson: string;
  reasoningChoicesJson: string;
  thinkingControlJson: string;
  requestFieldsWhenToolsPresentJson: string;
  combineSystemAndDeveloperMessages: boolean;
  thoughtBlockPattern: string;
}

/** Long-running local-model operations the settings page tracks page-level. */
export type ActiveModelOperationKind = 'add' | 'changeQuant';

/** Status endpoint the operation's id is valid against. */
export type ActiveModelOperationPollRoute = 'operations' | 'downloads';

export interface ActiveModelOperationState {
  operationId: string;
  routerModelId: string;
  catalogModelId: string;
  kind: ActiveModelOperationKind;
  pollRoute: ActiveModelOperationPollRoute;
}

export interface CanonicalLocalRuntimeConfig {
  routerModelId: string;
  runtimeProfileId?: string;
  loadParams?: Record<string, unknown>;
  parallelToolCalls?: boolean;
  routerContextSize?: number;
  routerCacheRamMib?: number;
}
