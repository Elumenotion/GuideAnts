import { describe, expect, it } from 'vitest';
import { buildLocalModelAddModelRequest, buildLocalModelOnboardingRequest } from '../buildCommand';
import { selectAttachableAliases } from '../selectors';
import { isLocalModelOnboardingInFlight, normalizeLocalModelOnboardingStatus } from '../status';
import { validateLocalModelOnboardingDraft } from '../validateDraft';

const explicitCustomDraft = {
  installSource: 'huggingface' as const,
  runtimeProfileId: 'qwen3_6',
  routerModelId: 'qwen3.6-9b-q5km',
  huggingFaceRepository: 'unsloth/Qwen3.6-9B-GGUF',
  huggingFaceResolvedRevision: 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef',
  huggingFaceArtifactGroupId: 'single::Qwen3-9B-Q5_K_M.gguf',
  huggingFaceModelFiles: ['Qwen3-9B-Q5_K_M.gguf'],
  huggingFaceMmprojFiles: [] as string[],
  huggingFaceTargetDirectory: 'qwen3.6-9b-q5km',
  huggingFaceRouterPresetRows: [{ key: 'ctx-size', value: '8192' }],
  huggingFacePresetMode: 'replace' as const,
  existingAliasRouterModelId: '',
  catalogModelId: 'qwen3.6-local',
  catalogDisplayName: 'Qwen 3.6 Local',
  catalogDescription: 'Local text model',
  catalogDisplayOrder: '5',
  catalogIsActive: true,
};

describe('buildLocalModelOnboardingRequest', () => {
  it('produces equivalent payload for settings and wizard inputs', () => {
    const fromSettings = buildLocalModelAddModelRequest(explicitCustomDraft, 'settings');
    const fromWizard = buildLocalModelAddModelRequest(explicitCustomDraft, 'wizard');

    const { providerConfig: settingsProviderConfig, ...settingsWithoutUi } = fromSettings;
    const { providerConfig: wizardProviderConfig, ...wizardWithoutUi } = fromWizard;

    expect(settingsProviderConfig).toEqual({ onboardingUi: 'settings' });
    expect(wizardProviderConfig).toEqual({ onboardingUi: 'wizard' });
    expect(settingsWithoutUi).toEqual(wizardWithoutUi);
    expect(fromSettings.install?.huggingFace?.modelFiles).toEqual(['Qwen3-9B-Q5_K_M.gguf']);
    expect(fromSettings.install?.routerContextSize).toBeUndefined();
  });

  it('defaults catalog and target directory from router alias when blank', () => {
    const request = buildLocalModelOnboardingRequest({
      ...explicitCustomDraft,
      catalogModelId: '',
      catalogDisplayName: '',
      huggingFaceTargetDirectory: '',
    });

    expect(request.catalog.modelId).toBe('qwen3.6-9b-q5km');
    expect(request.catalog.displayName).toBe('qwen3.6-9b-q5km');
    expect(request.install?.huggingFace?.targetDirectory).toBe('qwen3.6-9b-q5km');
  });

  it('allows text-only huggingface install without mmproj files', () => {
    const request = buildLocalModelOnboardingRequest(explicitCustomDraft);
    expect(request.install?.source).toBe('huggingface');
    expect(request.install?.huggingFace?.mmprojFiles).toEqual([]);
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

describe('validateLocalModelOnboardingDraft', () => {
  it('returns validation errors from build command', () => {
    expect(validateLocalModelOnboardingDraft({
      ...explicitCustomDraft,
      runtimeProfileId: '',
    })).toEqual(['Runtime profile is required for llama-cpp.']);
    expect(validateLocalModelOnboardingDraft(explicitCustomDraft)).toEqual([]);
  });
});
