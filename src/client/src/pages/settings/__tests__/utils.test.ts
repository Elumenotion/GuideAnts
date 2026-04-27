import { describe, expect, it } from 'vitest';
import {
  buildAddModelRequest,
  buildCatalogEditRequest,
  createEmptyAddModelWizardState,
  exportRuntimeProfile,
  importRuntimeProfile,
} from '../utils';

describe('buildAddModelRequest', () => {
  it('builds openai-chat request with reasoning toggle', () => {
    const state = createEmptyAddModelWizardState('openai-chat');
    state.catalogModelId = 'gpt-4o-chat';
    state.catalogDisplayName = 'GPT-4o Chat';
    state.openAiReasoningEffortEnabled = true;

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('openai-chat');
    expect(request.providerConfig).toEqual({ reasoningEffortEnabled: true });
    expect(request.install).toBeUndefined();
  });

  it('builds llama-cpp huggingface request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'qwen3.5-local';
    state.catalogDisplayName = 'Qwen3.5 Local';
    state.llamaInstallSource = 'huggingface';
    state.llamaRuntimeProfileId = 'qwen3_5';
    state.llamaRouterModelId = 'Qwen3.5-9B-Q5_K_M';
    state.llamaHuggingFaceRepository = 'unsloth/Qwen3.5-9B-GGUF';
    state.llamaHuggingFaceQuantIncludePattern = '*Q5_K_M*';
    state.llamaHuggingFaceMmprojIncludePattern = '*mmproj*';
    state.llamaHuggingFaceTargetDirectory = 'Qwen3.5-9B-Q5_K_M';

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('llama-cpp');
    expect(request.install?.source).toBe('huggingface');
    expect(request.install?.huggingFace?.repository).toBe('unsloth/Qwen3.5-9B-GGUF');
  });

  it('builds llama-cpp existingAlias request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'adopted-alias-model';
    state.catalogDisplayName = 'Adopted Alias Model';
    state.llamaInstallSource = 'existingAlias';
    state.llamaRuntimeProfileId = 'qwen3_5';
    state.llamaExistingAliasRouterModelId = 'Qwen3.5-9B-Q5_K_M';

    const request = buildAddModelRequest(state);

    expect(request.install?.source).toBe('existingAlias');
    expect(request.install?.routerModelId).toBe('Qwen3.5-9B-Q5_K_M');
  });
});

describe('buildCatalogEditRequest', () => {
  it('builds llama-cpp edit request with canonical localRuntimeJson', () => {
    const request = buildCatalogEditRequest({
      modelId: 'qwen-local',
      provider: 'llama-cpp',
      displayName: 'Qwen Local',
      description: '',
      displayOrder: '',
      isActive: true,
      openAiReasoningEffortEnabled: false,
      anthropicThinkingEnabled: false,
      localRuntimeRouterModelId: 'QwenAlias',
      localRuntimeProfileId: 'qwen3_5',
      localRuntimeLoadParamsJson: '{"model":"QwenAlias"}',
      localRuntimeParallelToolCalls: false,
      localRuntimeRouterContextSize: '',
      localRuntimeRouterCacheRamMib: '',
    });

    expect(request.modelId).toBe('qwen-local');
    expect(request.localRuntimeJson).toContain('"routerModelId": "QwenAlias"');
    expect(request.localRuntimeJson).toContain('"runtimeProfileId": "qwen3_5"');
  });
});

describe('runtime profile import/export helpers', () => {
  it('imports an exported runtime profile dto into form state', () => {
    const json = exportRuntimeProfile({
      profileId: 'qwen3_6',
      displayName: 'Qwen 3.6',
      description: 'Template profile',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
      samplingParametersJson: '{"temperature":{"kind":"number","defaultValue":0.7}}',
      thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"minimal":[],"medium":[]}}',
      created: '2026-04-22T00:00:00Z',
      updated: '2026-04-22T00:00:00Z',
    });

    expect(importRuntimeProfile(json)).toEqual({
      profileId: 'qwen3_6',
      displayName: 'Qwen 3.6',
      description: 'Template profile',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
      samplingParametersJson: '{"temperature":{"kind":"number","defaultValue":0.7}}',
      thinkingControlJson: '{"defaultChoice":"medium","choiceActions":{"minimal":[],"medium":[]}}',
    });
  });

  it('rejects import payloads that do not match the profile contract', () => {
    expect(() =>
      importRuntimeProfile(
        JSON.stringify({
          profileId: 'qwen3_6',
          displayName: 'Qwen 3.6',
          combineSystemAndDeveloperMessages: true,
          samplingParametersJson: { temperature: 0.7 },
          thinkingControlJson: '{}',
        })
      )
    ).toThrow('samplingParametersJson');
  });
});
