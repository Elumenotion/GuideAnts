import { describe, expect, it } from 'vitest';
import { createEmptyAddModelWizardState, buildAddModelRequest as buildSettingsAddModelRequest } from '../../../../pages/settings/utils';
import { buildLocalAiModelRequest } from '../utils';
import { buildCuratedAddModelRequest } from '../../../../features/localModelOnboarding/curated/buildCuratedRequest';
import { catalogFixture } from '../../../../features/localModelOnboarding/curated/fixtures';
import type { LlamaCatalogDefinitionDto, LlamaCatalogResponseDto } from '../../../../types/settings';

describe('local onboarding cross-UI parity', () => {
  it('builds equivalent curated payloads for settings and wizard entry points', () => {
    const catalog = catalogFixture as LlamaCatalogResponseDto;
    const definition = catalog.models[1] as LlamaCatalogDefinitionDto;

    const fromSettings = buildCuratedAddModelRequest(
      definition,
      catalog.catalogVersion,
      'q6_k_xl',
      '8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a',
      { onboardingUi: 'settings' },
    );
    const fromWizard = buildCuratedAddModelRequest(
      definition,
      catalog.catalogVersion,
      'q6_k_xl',
      '8f4c3f1a2b3c4d5e6f708192a3b4c5d6e7f8091a',
      { onboardingUi: 'wizard' },
    );

    expect(fromSettings.install).toEqual(fromWizard.install);
    expect(fromSettings.catalog).toEqual(fromWizard.catalog);
    expect(fromSettings.providerConfig).toEqual({ onboardingUi: 'settings' });
    expect(fromWizard.providerConfig).toEqual({ onboardingUi: 'wizard' });
  });

  it('builds identical payloads for explicit custom huggingface intent', () => {
    const settings = createEmptyAddModelWizardState('llama-cpp');
    settings.catalogModelId = 'qwen3.6-local';
    settings.catalogDisplayName = 'Qwen 3.6 Local';
    settings.catalogDescription = 'Local chat model';
    settings.catalogDisplayOrder = '3';
    settings.catalogIsActive = true;
    settings.llamaInstallSource = 'huggingface';
    settings.runtimeProfileId = 'qwen3_6';
    settings.llamaRouterModelId = 'qwen3.6-local';
    settings.llamaHuggingFaceRepository = 'unsloth/Qwen3.6-9B-GGUF';
    settings.llamaHuggingFaceResolvedRevision = 'abc123def';
    settings.llamaHuggingFaceArtifactGroupId = 'q5-k-m-group';
    settings.llamaHuggingFaceModelFiles = ['Qwen3.6-9B-Q5_K_M.gguf'];
    settings.llamaHuggingFaceMmprojFiles = [];
    settings.llamaHuggingFaceTargetDirectory = 'qwen3.6-local';
    settings.llamaHuggingFaceRouterPresetRows = [{ key: 'ctx-size', value: '8192' }];
    settings.llamaHuggingFacePresetMode = 'replace';

    const wizard = {
      localId: 'draft-1',
      persisted: false,
      asyncOperationId: null,
      asyncStatus: 'submitted' as const,
      asyncProgress: null,
      asyncError: null,
      setAsGlobalDefault: false,
      installSource: 'huggingface' as const,
      routerModelId: 'qwen3.6-local',
      runtimeProfileId: 'qwen3_6',
      huggingFaceRepository: 'unsloth/Qwen3.6-9B-GGUF',
      huggingFaceResolvedRevision: 'abc123def',
      huggingFaceArtifactGroupId: 'q5-k-m-group',
      huggingFaceModelFiles: ['Qwen3.6-9B-Q5_K_M.gguf'],
      huggingFaceMmprojFiles: [],
      huggingFaceTargetDirectory: 'qwen3.6-local',
      huggingFaceRouterPresetRows: [{ key: 'ctx-size', value: '8192' }],
      huggingFacePresetMode: 'replace' as const,
      existingAliasRouterModelId: '',
      catalogModelId: 'qwen3.6-local',
      catalogDisplayName: 'Qwen 3.6 Local',
    };

    const fromSettings = buildSettingsAddModelRequest(settings);
    const fromWizard = buildLocalAiModelRequest(wizard);

    expect(fromSettings).toEqual({
      ...fromWizard,
      catalog: {
        ...fromWizard.catalog,
        description: 'Local chat model',
        displayOrder: 3,
      },
      providerConfig: { onboardingUi: 'settings' },
    });
  });

  it('builds identical payloads for equivalent existing-alias intent', () => {
    const settings = createEmptyAddModelWizardState('llama-cpp');
    settings.catalogModelId = 'qwen3.6-attached';
    settings.catalogDisplayName = 'Qwen 3.6 Attached';
    settings.catalogIsActive = true;
    settings.llamaInstallSource = 'existingAlias';
    settings.runtimeProfileId = 'qwen3_6';
    settings.llamaExistingAliasRouterModelId = 'qwen3.6-runtime';

    const wizard = {
      localId: 'draft-2',
      persisted: false,
      asyncOperationId: null,
      asyncStatus: 'submitted' as const,
      asyncProgress: null,
      asyncError: null,
      setAsGlobalDefault: false,
      installSource: 'existingAlias' as const,
      routerModelId: '',
      runtimeProfileId: 'qwen3_6',
      huggingFaceRepository: '',
      huggingFaceResolvedRevision: '',
      huggingFaceArtifactGroupId: '',
      huggingFaceModelFiles: [],
      huggingFaceMmprojFiles: [],
      huggingFaceTargetDirectory: '',
      huggingFaceRouterPresetRows: [],
      huggingFacePresetMode: 'replace' as const,
      existingAliasRouterModelId: 'qwen3.6-runtime',
      catalogModelId: 'qwen3.6-attached',
      catalogDisplayName: 'Qwen 3.6 Attached',
    };

    const fromSettings = buildSettingsAddModelRequest(settings);
    const fromWizard = buildLocalAiModelRequest(wizard);
    expect(fromSettings).toEqual({
      ...fromWizard,
      providerConfig: { onboardingUi: 'settings' },
    });
  });

  it('builds identical payloads for multimodal explicit custom huggingface intent', () => {
    const settings = createEmptyAddModelWizardState('llama-cpp');
    settings.catalogModelId = 'llava-local';
    settings.catalogDisplayName = 'LLaVA Local';
    settings.catalogIsActive = true;
    settings.llamaInstallSource = 'huggingface';
    settings.runtimeProfileId = 'qwen3_6';
    settings.llamaRouterModelId = 'llava-local';
    settings.llamaHuggingFaceRepository = 'lmstudio-community/llava-v1.6-gguf';
    settings.llamaHuggingFaceResolvedRevision = 'rev-mm';
    settings.llamaHuggingFaceArtifactGroupId = 'q4-group';
    settings.llamaHuggingFaceModelFiles = ['llava-Q4_K_M.gguf'];
    settings.llamaHuggingFaceMmprojFiles = ['mmproj.gguf'];
    settings.llamaHuggingFaceTargetDirectory = 'llava-local';
    settings.llamaHuggingFaceRouterPresetRows = [{ key: 'ctx-size', value: '4096' }];
    settings.llamaHuggingFacePresetMode = 'merge';

    const wizard = {
      localId: 'draft-3',
      persisted: false,
      asyncOperationId: null,
      asyncStatus: 'submitted' as const,
      asyncProgress: null,
      asyncError: null,
      setAsGlobalDefault: false,
      installSource: 'huggingface' as const,
      routerModelId: 'llava-local',
      runtimeProfileId: 'qwen3_6',
      huggingFaceRepository: 'lmstudio-community/llava-v1.6-gguf',
      huggingFaceResolvedRevision: 'rev-mm',
      huggingFaceArtifactGroupId: 'q4-group',
      huggingFaceModelFiles: ['llava-Q4_K_M.gguf'],
      huggingFaceMmprojFiles: ['mmproj.gguf'],
      huggingFaceTargetDirectory: 'llava-local',
      huggingFaceRouterPresetRows: [{ key: 'ctx-size', value: '4096' }],
      huggingFacePresetMode: 'merge' as const,
      existingAliasRouterModelId: '',
      catalogModelId: 'llava-local',
      catalogDisplayName: 'LLaVA Local',
    };

    const fromSettings = buildSettingsAddModelRequest(settings);
    const fromWizard = buildLocalAiModelRequest(wizard);
    expect(fromSettings).toEqual({
      ...fromWizard,
      providerConfig: { onboardingUi: 'settings' },
    });
  });
});
