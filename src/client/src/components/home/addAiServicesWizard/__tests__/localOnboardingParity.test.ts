import { describe, expect, it } from 'vitest';
import { createEmptyAddModelWizardState, buildAddModelRequest as buildSettingsAddModelRequest } from '../../../../pages/settings/utils';
import { buildLocalAiModelRequest } from '../utils';

describe('local onboarding cross-UI parity', () => {
  it('builds identical payloads for equivalent huggingface intent', () => {
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
    settings.llamaHuggingFaceQuantIncludePattern = '*Q5_K_M*';
    settings.llamaHuggingFaceMmprojIncludePattern = '';
    settings.llamaHuggingFaceTargetDirectory = 'qwen3.6-local';
    settings.llamaRouterContextSize = '8192';
    settings.llamaRouterCacheRamMib = '1024';

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
      huggingFaceQuantIncludePattern: '*Q5_K_M*',
      huggingFaceMmprojIncludePattern: '',
      huggingFaceTargetDirectory: 'qwen3.6-local',
      existingAliasRouterModelId: '',
      routerContextSize: '8192',
      routerCacheRamMib: '1024',
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
    settings.llamaRouterContextSize = '16384';
    settings.llamaRouterCacheRamMib = '2048';

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
      huggingFaceQuantIncludePattern: '',
      huggingFaceMmprojIncludePattern: '',
      huggingFaceTargetDirectory: '',
      existingAliasRouterModelId: 'qwen3.6-runtime',
      routerContextSize: '16384',
      routerCacheRamMib: '2048',
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

  it('builds identical payloads for equivalent multimodal huggingface intent', () => {
    const settings = createEmptyAddModelWizardState('llama-cpp');
    settings.catalogModelId = 'llava-local';
    settings.catalogDisplayName = 'LLaVA Local';
    settings.catalogIsActive = true;
    settings.llamaInstallSource = 'huggingface';
    settings.runtimeProfileId = 'qwen3_6';
    settings.llamaRouterModelId = 'llava-local';
    settings.llamaHuggingFaceRepository = 'lmstudio-community/llava-v1.6-gguf';
    settings.llamaHuggingFaceQuantIncludePattern = '*Q4_K_M*';
    settings.llamaHuggingFaceMmprojIncludePattern = '*mmproj*';
    settings.llamaHuggingFaceTargetDirectory = 'llava-local';

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
      huggingFaceQuantIncludePattern: '*Q4_K_M*',
      huggingFaceMmprojIncludePattern: '*mmproj*',
      huggingFaceTargetDirectory: 'llava-local',
      existingAliasRouterModelId: '',
      routerContextSize: '',
      routerCacheRamMib: '',
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
