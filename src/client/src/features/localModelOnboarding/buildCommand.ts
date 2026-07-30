import type { AddModelRequest } from '../../types/settings';
import type { LocalModelOnboardingDraft } from './contracts';
import { presetRecordFromRows } from './routerPreset';
import { normalizeParameterSurface } from '../../pages/settings/parameterSurface';

export interface LocalModelOnboardingDefaultOptions {
  defaultCatalogModelId: string;
  defaultCatalogDisplayName: string;
  defaultTargetDirectory: string;
}

export function resolveLocalModelOnboardingDefaults(
  draft: LocalModelOnboardingDraft
): LocalModelOnboardingDefaultOptions {
  if (draft.installSource === 'existingAlias') {
    const alias = draft.existingAliasRouterModelId.trim();
    return {
      defaultCatalogModelId: alias,
      defaultCatalogDisplayName: alias,
      defaultTargetDirectory: '',
    };
  }

  const routerModelId = draft.routerModelId.trim();
  return {
    defaultCatalogModelId: routerModelId,
    defaultCatalogDisplayName: routerModelId,
    defaultTargetDirectory: routerModelId,
  };
}

export function buildLocalModelAddModelRequest(
  draft: LocalModelOnboardingDraft,
  onboardingUi: 'settings' | 'wizard'
): AddModelRequest {
  return buildLocalModelOnboardingRequest(draft, {
    onboardingUi,
    defaultCatalogIsActive: draft.catalogIsActive ?? true,
  });
}

function normalizeOptionalString(value: string | undefined): string | undefined {
  const trimmed = (value ?? '').trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function normalizeDisplayOrder(value: string | undefined): number | undefined {
  const trimmed = (value ?? '').trim();
  if (!trimmed) {
    return undefined;
  }

  const parsed = Number(trimmed);
  if (!Number.isInteger(parsed)) {
    throw new Error('Display order must be a whole number.');
  }

  return parsed;
}

function isExplicitCustomInstall(draft: LocalModelOnboardingDraft): boolean {
  return draft.huggingFaceModelFiles.length > 0
    && draft.huggingFaceResolvedRevision.trim().length > 0
    && draft.huggingFaceRouterPresetRows.some((row) => row.key.trim().length > 0);
}

function buildModelChatBehaviorProviderConfig(draft: LocalModelOnboardingDraft): Record<string, unknown> {
  const parameterSurface = normalizeParameterSurface({
    samplingParametersJson: draft.samplingParametersJson,
    reasoningChoicesJson: draft.reasoningChoicesJson,
  });
  const thinkingControlJson = draft.thinkingControlJson.trim() || '{}';
  const requestFieldsWhenToolsPresentJson = draft.requestFieldsWhenToolsPresentJson.trim() || '{}';
  for (const [label, json] of [
    ['Thinking control JSON', thinkingControlJson],
    ['Extra request fields JSON', requestFieldsWhenToolsPresentJson],
  ] as const) {
    try {
      const parsed = JSON.parse(json) as unknown;
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
        throw new Error(`${label} must be a JSON object.`);
      }
    } catch (error) {
      if (error instanceof Error && error.message.includes('must be')) {
        throw error;
      }
      throw new Error(`${label} must be valid JSON.`);
    }
  }

  const providerConfig: Record<string, unknown> = {
    samplingParametersJson: parameterSurface.samplingParametersJson,
    thinkingControlJson,
    requestFieldsWhenToolsPresentJson,
    combineSystemAndDeveloperMessages: draft.combineSystemAndDeveloperMessages,
  };
  if (parameterSurface.reasoningChoicesJson) {
    providerConfig.reasoningChoicesJson = parameterSurface.reasoningChoicesJson;
  }
  const thoughtBlockPattern = draft.thoughtBlockPattern.trim();
  if (thoughtBlockPattern) {
    providerConfig.thoughtBlockPattern = thoughtBlockPattern;
  }
  return providerConfig;
}

export function buildLocalModelOnboardingRequest(
  draft: LocalModelOnboardingDraft,
  options?: {
    onboardingUi?: 'settings' | 'wizard';
    defaultCatalogModelId?: string;
    defaultCatalogDisplayName?: string;
    defaultTargetDirectory?: string;
    defaultCatalogIsActive?: boolean;
  }
): AddModelRequest {
  const source = draft.installSource;
  const resolvedDefaults = resolveLocalModelOnboardingDefaults(draft);
  const fallbackCatalogModelId = (
    options?.defaultCatalogModelId ?? resolvedDefaults.defaultCatalogModelId
  ).trim();
  const catalogModelId = draft.catalogModelId.trim() || fallbackCatalogModelId;
  if (!catalogModelId) {
    throw new Error('Catalog Model ID is required.');
  }

  const fallbackCatalogDisplayName = (
    options?.defaultCatalogDisplayName ?? resolvedDefaults.defaultCatalogDisplayName ?? fallbackCatalogModelId
  ).trim();
  const catalogDisplayName = draft.catalogDisplayName.trim() || fallbackCatalogDisplayName || catalogModelId;
  if (!catalogDisplayName) {
    throw new Error('Catalog display name is required.');
  }

  const chatBehavior = buildModelChatBehaviorProviderConfig(draft);
  const providerConfig: Record<string, unknown> = {
    ...chatBehavior,
  };
  if (options?.onboardingUi) {
    providerConfig.onboardingUi = options.onboardingUi;
  }

  const request: AddModelRequest = {
    provider: 'llama-cpp',
    catalog: {
      modelId: catalogModelId,
      displayName: catalogDisplayName,
      description: normalizeOptionalString(draft.catalogDescription),
      displayOrder: normalizeDisplayOrder(draft.catalogDisplayOrder),
      isActive: draft.catalogIsActive ?? options?.defaultCatalogIsActive ?? true,
    },
    providerConfig,
  };

  if (source === 'existingAlias') {
    const alias = draft.existingAliasRouterModelId.trim();
    if (!alias) {
      throw new Error('Pick an existing alias to attach.');
    }

    request.install = {
      source: 'existingAlias',
      routerModelId: alias,
      existingAlias: { routerModelId: alias },
    };
    return request;
  }

  const routerModelId = draft.routerModelId.trim();
  if (!routerModelId) {
    throw new Error('Router alias is required for Hugging Face install.');
  }

  const repository = draft.huggingFaceRepository.trim();
  const fallbackTargetDirectory = (
    options?.defaultTargetDirectory ?? resolvedDefaults.defaultTargetDirectory
  ).trim();
  const targetDirectory = draft.huggingFaceTargetDirectory.trim() || fallbackTargetDirectory;

  if (!repository) {
    throw new Error('Hugging Face repository is required.');
  }
  if (!targetDirectory) {
    throw new Error('Target directory is required. It defaults to the router alias when left blank.');
  }

  if (isExplicitCustomInstall(draft)) {
    const resolvedRevision = draft.huggingFaceResolvedRevision.trim();
    const routerPreset = presetRecordFromRows(draft.huggingFaceRouterPresetRows);
    if (Object.keys(routerPreset).length === 0) {
      throw new Error('Alias preset is required for custom Hugging Face install.');
    }

    request.install = {
      source: 'huggingface',
      routerModelId,
      presetMode: draft.huggingFacePresetMode,
      huggingFace: {
        repository,
        resolvedRevision,
        modelFiles: draft.huggingFaceModelFiles,
        mmprojFiles: draft.huggingFaceMmprojFiles,
        targetDirectory,
        routerPreset,
        quantIncludePattern: '',
        mmprojIncludePattern: '',
      },
    };
    return request;
  }

  throw new Error(
    'Complete custom Hugging Face fields: revision, artifact group, alias preset, alias, and model chat behavior.'
  );
}
