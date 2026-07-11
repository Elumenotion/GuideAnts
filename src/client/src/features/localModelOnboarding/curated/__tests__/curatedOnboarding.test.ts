import { catalogFixture, quantFixture, curatedRequestFixture, operationFixture } from '../fixtures';
import { describe, expect, it } from 'vitest';
import {
  assertCuratedRequestShape,
  buildCuratedAddModelRequest,
  CURATED_REQUEST_ALLOWED_FIELDS,
} from '../buildCuratedRequest';
import { filterCatalogModels, isQuantRecommended } from '../types';
import type { LlamaCatalogDefinitionDto, LlamaCatalogResponseDto } from '../../../../types/settings';

const catalog = catalogFixture as LlamaCatalogResponseDto;
    const definition = catalog.models[1] as LlamaCatalogDefinitionDto;

describe('buildCuratedAddModelRequest', () => {
  it('matches Phase 4 curated-add fixture shape with nested curated payload', () => {
    const request = buildCuratedAddModelRequest(
      definition,
      catalog.catalogVersion,
      curatedRequestFixture.install.quantId,
      curatedRequestFixture.install.resolvedRevision,
      { onboardingUi: 'settings' },
    );

    expect(request.provider).toBe(curatedRequestFixture.provider);
    expect(request.catalog.displayName).toBe(curatedRequestFixture.catalog.displayName);
    expect(request.catalog.isActive).toBe(curatedRequestFixture.catalog.isActive);
    expect(request.install?.source).toBe('curated');
    expect(request.install?.curated).toEqual({
      catalogId: curatedRequestFixture.install.catalogId,
      catalogVersion: curatedRequestFixture.install.catalogVersion,
      quantId: curatedRequestFixture.install.quantId,
      resolvedRevision: curatedRequestFixture.install.resolvedRevision,
    });
    expect(() => assertCuratedRequestShape(request)).not.toThrow();
  });

  it('forbids repository, alias, profile, and path fields', () => {
    const request = buildCuratedAddModelRequest(
      definition,
      catalog.catalogVersion,
      'q6_k_xl',
      quantFixture.resolvedRevision,
    );
    expect(request.install?.routerModelId).toBeUndefined();
    expect(request.install?.runtimeProfileId).toBeUndefined();
    expect(request.install?.huggingFace).toBeUndefined();
    expect(request.install?.existingAlias).toBeUndefined();
    expect(CURATED_REQUEST_ALLOWED_FIELDS).toContain('install.curated.quantId');
  });
});

describe('catalog search and recommendation display', () => {
  it('filters models by search query', () => {
    const results = filterCatalogModels(catalog.models, 'mtp');
    expect(results).toHaveLength(1);
    expect(results[0]?.id).toBe('qwen3.6-35b-a3b-mtp');
  });

  it('treats recommendation labels as display-only', () => {
    const mtp = catalog.models[1] as LlamaCatalogDefinitionDto;
    const recommended = isQuantRecommended('UD-Q4_K_XL', mtp.quantMetadata.recommendedLabels);
    const notRecommended = isQuantRecommended('Q4_K_M', mtp.quantMetadata.recommendedLabels);
    expect(recommended).toBe(true);
    expect(notRecommended).toBe(false);
  });
});

describe('operation fixture', () => {
  it('includes canonical operation stages used by progress UI', () => {
    expect(operationFixture.status).toBe('downloading');
    expect(operationFixture.operationId).toMatch(/^[a-f0-9-]+$/i);
  });
});
