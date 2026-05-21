import type { AddModelRequest } from '../../types/settings';
import type { LocalModelOnboardingDraft } from './contracts';

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

function parseOptionalRouterContextSize(raw: string): number | undefined {
  const t = raw.trim();
  if (t.length === 0) {
    return undefined;
  }
  const n = Number(t);
  if (!Number.isInteger(n) || n < 1024 || n > 1_048_576) {
    throw new Error('Context size must be a whole number from 1024 to 1048576, or blank to use the container default.');
  }
  return n;
}

function parseOptionalRouterCacheRamMib(raw: string): number | undefined {
  const t = raw.trim();
  if (t.length === 0) {
    return undefined;
  }
  const n = Number(t);
  if (!Number.isInteger(n) || n < 0 || n > 262_144) {
    throw new Error('Prompt cache RAM (MiB) must be a whole number from 0 to 262144, or blank to use the container default.');
  }
  return n;
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
  const runtimeProfileId = draft.runtimeProfileId.trim();
  if (!runtimeProfileId) {
    throw new Error('Runtime profile is required for llama-cpp.');
  }

  const routerContextSize = parseOptionalRouterContextSize(draft.routerContextSize);
  const routerCacheRamMib = parseOptionalRouterCacheRamMib(draft.routerCacheRamMib);
  const routerKnobs: { routerContextSize?: number; routerCacheRamMib?: number } = {};
  if (routerContextSize !== undefined) {
    routerKnobs.routerContextSize = routerContextSize;
  }
  if (routerCacheRamMib !== undefined) {
    routerKnobs.routerCacheRamMib = routerCacheRamMib;
  }

  const source = draft.installSource;
  const fallbackCatalogModelId = (options?.defaultCatalogModelId ?? '').trim();
  const catalogModelId = draft.catalogModelId.trim() || fallbackCatalogModelId;
  if (!catalogModelId) {
    throw new Error('Catalog Model ID is required.');
  }

  const fallbackCatalogDisplayName = (options?.defaultCatalogDisplayName ?? fallbackCatalogModelId).trim();
  const catalogDisplayName = draft.catalogDisplayName.trim() || fallbackCatalogDisplayName || catalogModelId;
  if (!catalogDisplayName) {
    throw new Error('Catalog display name is required.');
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
    providerConfig: options?.onboardingUi
      ? {
          onboardingUi: options.onboardingUi,
        }
      : undefined,
  };

  if (source === 'existingAlias') {
    const alias = draft.existingAliasRouterModelId.trim();
    if (!alias) {
      throw new Error('Pick an existing alias to attach.');
    }

    request.install = {
      source: 'existingAlias',
      routerModelId: alias,
      runtimeProfileId,
      existingAlias: { routerModelId: alias },
      ...routerKnobs,
    };
    return request;
  }

  const routerModelId = draft.routerModelId.trim();
  if (!routerModelId) {
    throw new Error('Router alias is required for Hugging Face install.');
  }

  const repository = draft.huggingFaceRepository.trim();
  const quantPattern = draft.huggingFaceQuantIncludePattern.trim();
  const mmprojPattern = draft.huggingFaceMmprojIncludePattern.trim();
  const fallbackTargetDirectory = (options?.defaultTargetDirectory ?? '').trim();
  const targetDirectory = draft.huggingFaceTargetDirectory.trim() || fallbackTargetDirectory;

  if (!repository || !quantPattern || !targetDirectory) {
    throw new Error('Repository, quant pattern, and target directory are required. mmproj pattern is optional.');
  }

  request.install = {
    source: 'huggingface',
    routerModelId,
    runtimeProfileId,
    huggingFace: {
      repository,
      quantIncludePattern: quantPattern,
      mmprojIncludePattern: mmprojPattern,
      targetDirectory,
    },
    ...routerKnobs,
  };

  return request;
}
