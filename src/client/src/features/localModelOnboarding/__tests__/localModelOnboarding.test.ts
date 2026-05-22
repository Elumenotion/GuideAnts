import { describe, expect, it } from 'vitest';
import { buildLocalModelOnboardingRequest } from '../buildCommand';
import { selectAttachableAliases } from '../selectors';
import { isLocalModelOnboardingInFlight, normalizeLocalModelOnboardingStatus } from '../status';

describe('buildLocalModelOnboardingRequest', () => {
  it('produces equivalent payload for settings and wizard inputs', () => {
    const base = {
      installSource: 'huggingface' as const,
      runtimeProfileId: 'qwen3_6',
      routerModelId: 'qwen3.6-9b-q5km',
      huggingFaceRepository: 'unsloth/Qwen3.6-9B-GGUF',
      huggingFaceQuantIncludePattern: '*Q5_K_M*',
      huggingFaceMmprojIncludePattern: '',
      huggingFaceTargetDirectory: 'qwen3.6-9b-q5km',
      existingAliasRouterModelId: '',
      routerContextSize: '8192',
      routerCacheRamMib: '1024',
      catalogModelId: 'qwen3.6-local',
      catalogDisplayName: 'Qwen 3.6 Local',
      catalogDescription: 'Local text model',
      catalogDisplayOrder: '5',
      catalogIsActive: true,
    };

    const fromSettings = buildLocalModelOnboardingRequest(base, { onboardingUi: 'settings' });
    const fromWizard = buildLocalModelOnboardingRequest(base, {
      onboardingUi: 'wizard',
      defaultCatalogModelId: base.routerModelId,
      defaultCatalogDisplayName: base.routerModelId,
      defaultTargetDirectory: base.routerModelId,
      defaultCatalogIsActive: true,
    });

    const { providerConfig: settingsProviderConfig, ...settingsWithoutUi } = fromSettings;
    const { providerConfig: wizardProviderConfig, ...wizardWithoutUi } = fromWizard;

    expect(settingsProviderConfig).toEqual({ onboardingUi: 'settings' });
    expect(wizardProviderConfig).toEqual({ onboardingUi: 'wizard' });
    expect(settingsWithoutUi).toEqual(wizardWithoutUi);
  });

  it('allows text-only huggingface install without mmproj pattern', () => {
    const request = buildLocalModelOnboardingRequest({
      installSource: 'huggingface',
      runtimeProfileId: 'qwen3_5',
      routerModelId: 'qwen3.5-local',
      huggingFaceRepository: 'unsloth/Qwen3.5-9B-GGUF',
      huggingFaceQuantIncludePattern: '*Q4_K_M*',
      huggingFaceMmprojIncludePattern: '',
      huggingFaceTargetDirectory: 'qwen3.5-local',
      existingAliasRouterModelId: '',
      routerContextSize: '',
      routerCacheRamMib: '',
      catalogModelId: 'qwen3.5-local',
      catalogDisplayName: 'Qwen 3.5 Local',
      catalogIsActive: true,
    });

    expect(request.install?.source).toBe('huggingface');
    expect(request.install?.huggingFace?.mmprojIncludePattern).toBe('');
  });
});

describe('selectAttachableAliases', () => {
  it('returns only aliases with model file and no catalog attachments', () => {
    const rows = [
      {
        routerModelId: 'alias-a',
        runtimeState: 'unloaded',
        hasModelFile: true,
        hasMmprojFile: false,
        catalogModelIds: [],
        notebookReferenceCount: 0,
        modelPath: '/models/a.gguf',
        mmprojPath: null,
      },
      {
        routerModelId: 'alias-b',
        runtimeState: 'loaded',
        hasModelFile: true,
        hasMmprojFile: true,
        catalogModelIds: ['model-b'],
        notebookReferenceCount: 0,
        modelPath: '/models/b.gguf',
        mmprojPath: '/models/b-mmproj.gguf',
      },
    ];

    const result = selectAttachableAliases(rows as any);
    expect(result.map((row) => row.routerModelId)).toEqual(['alias-a']);
  });
});

describe('status helpers', () => {
  it('normalizes known runtime statuses and in-flight checks', () => {
    expect(normalizeLocalModelOnboardingStatus('resolving')).toBe('resolvingFiles');
    expect(normalizeLocalModelOnboardingStatus('registering')).toBe('registeringAlias');
    expect(normalizeLocalModelOnboardingStatus('failed')).toBe('error');

    expect(isLocalModelOnboardingInFlight('submitted')).toBe(true);
    expect(isLocalModelOnboardingInFlight('downloading')).toBe(true);
    expect(isLocalModelOnboardingInFlight('completed')).toBe(false);
    expect(isLocalModelOnboardingInFlight('error')).toBe(false);
  });
});
