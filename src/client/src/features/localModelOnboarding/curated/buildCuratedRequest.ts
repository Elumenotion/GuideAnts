import type { AddModelRequest, LlamaCatalogDefinitionDto } from '../../../types/settings';

const CURATED_FORBIDDEN_INSTALL_KEYS = [
  'routerModelId',
  'huggingFace',
  'existingAlias',
  'routerContextSize',
  'routerCacheRamMib',
] as const;

const CURATED_FORBIDDEN_CATALOG_KEYS = ['modelId', 'description', 'displayOrder'] as const;

export function buildCuratedAddModelRequest(
  definition: LlamaCatalogDefinitionDto,
  catalogVersion: string,
  quantId: string,
  resolvedRevision: string,
  options?: { onboardingUi?: 'settings' | 'wizard' }
): AddModelRequest {
  const request: AddModelRequest = {
    provider: 'llama-cpp',
    catalog: {
      modelId: '',
      displayName: definition.display.name,
      isActive: true,
    },
    install: {
      source: 'curated',
      curated: {
        catalogId: definition.id,
        catalogVersion,
        quantId,
        resolvedRevision,
      },
    },
    providerConfig: options?.onboardingUi
      ? { onboardingUi: options.onboardingUi }
      : undefined,
  };
  return request;
}

export function assertCuratedRequestShape(request: AddModelRequest): void {
  if (request.provider !== 'llama-cpp') {
    throw new Error('Curated install requires llama-cpp provider.');
  }
  if (!request.install || request.install.source !== 'curated') {
    throw new Error('Curated install requires install.source curated.');
  }
  if (!request.install.curated) {
    throw new Error('Curated install requires install.curated payload.');
  }

  for (const key of CURATED_FORBIDDEN_INSTALL_KEYS) {
    if (key in request.install && (request.install as unknown as Record<string, unknown>)[key] != null) {
      throw new Error(`Curated install must not include install.${key}.`);
    }
  }

  for (const key of CURATED_FORBIDDEN_CATALOG_KEYS) {
    const value = (request.catalog as unknown as Record<string, unknown>)[key];
    if (value != null && value !== '') {
      throw new Error(`Curated install must not include catalog.${key}.`);
    }
  }
}

export const CURATED_REQUEST_ALLOWED_FIELDS = [
  'provider',
  'catalog.displayName',
  'catalog.isActive',
  'install.source',
  'install.curated.catalogId',
  'install.curated.catalogVersion',
  'install.curated.quantId',
  'install.curated.resolvedRevision',
  'providerConfig.onboardingUi',
] as const;
