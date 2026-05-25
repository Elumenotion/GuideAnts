import { describe, expect, it } from 'vitest';
import {
  buildAddModelRequest,
  buildCatalogEditRequest,
  createEmptyAddModelWizardState,
  exportRuntimeProfile,
  importRuntimeProfile,
  parseCanonicalLocalRuntimeJson,
  parseRuntimeProfileId,
} from '../utils';

describe('buildAddModelRequest', () => {
  it('builds openai-chat request with runtime profile', () => {
    const state = createEmptyAddModelWizardState('openai-chat');
    state.catalogModelId = 'gpt-4o-chat';
    state.catalogDisplayName = 'GPT-4o Chat';
    state.runtimeProfileId = 'openai_default';

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('openai-chat');
    expect(request.providerConfig).toEqual({ runtimeProfileId: 'openai_default' });
    expect(request.install).toBeUndefined();
  });

  it('defaults anthropic add-model state with expected empty fields', () => {
    const state = createEmptyAddModelWizardState('anthropic');

    expect(state.provider).toBe('anthropic');
    expect(state.runtimeProfileId).toBe('');
    expect(state.catalogIsActive).toBe(true);
  });

  it('builds llama-cpp huggingface request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'qwen3.5-local';
    state.catalogDisplayName = 'Qwen3.5 Local';
    state.llamaInstallSource = 'huggingface';
    state.runtimeProfileId = 'qwen3_5';
    state.llamaRouterModelId = 'Qwen3.5-9B-Q5_K_M';
    state.llamaHuggingFaceRepository = 'unsloth/Qwen3.5-9B-GGUF';
    state.llamaHuggingFaceQuantIncludePattern = '*Q5_K_M*';
    state.llamaHuggingFaceMmprojIncludePattern = '';
    state.llamaHuggingFaceTargetDirectory = 'Qwen3.5-9B-Q5_K_M';

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('llama-cpp');
    expect(request.providerConfig).toEqual({ onboardingUi: 'settings' });
    expect(request.install?.source).toBe('huggingface');
    expect(request.install?.huggingFace?.repository).toBe('unsloth/Qwen3.5-9B-GGUF');
  });

  it('builds llama-cpp existingAlias request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'adopted-alias-model';
    state.catalogDisplayName = 'Adopted Alias Model';
    state.llamaInstallSource = 'existingAlias';
    state.runtimeProfileId = 'qwen3_5';
    state.llamaExistingAliasRouterModelId = 'Qwen3.5-9B-Q5_K_M';

    const request = buildAddModelRequest(state);

    expect(request.install?.source).toBe('existingAlias');
    expect(request.install?.routerModelId).toBe('Qwen3.5-9B-Q5_K_M');
  });
});

describe('buildCatalogEditRequest', () => {
  it('builds llama-cpp edit request with canonical runtimeConfigJson', () => {
    const request = buildCatalogEditRequest({
      modelId: 'qwen-local',
      provider: 'llama-cpp',
      displayName: 'Qwen Local',
      description: '',
      displayOrder: '',
      isActive: true,
      runtimeProfileId: 'qwen3_5',
      localRuntimeRouterModelId: 'QwenAlias',
      localRuntimeLoadParamsJson: '{"model":"QwenAlias"}',
      localRuntimeParallelToolCalls: false,
      localRuntimeRouterContextSize: '',
      localRuntimeRouterCacheRamMib: '',
    });

    expect(request.modelId).toBe('qwen-local');
    expect(request.runtimeConfigJson).toContain('"routerModelId": "QwenAlias"');
    expect(request.runtimeConfigJson).toContain('"runtimeProfileId": "qwen3_5"');
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
      providers: [],
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
      providers: [],
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

  it('parses runtime profile id from legacy PascalCase runtime config', () => {
    expect(parseRuntimeProfileId('{"RuntimeProfileId":"openai_chat_standard"}')).toBe('openai_chat_standard');
  });

  it('imports canonical local runtime config from legacy PascalCase keys', () => {
    const parsed = parseCanonicalLocalRuntimeJson(
      '{"RouterModelId":"QwenAlias","RuntimeProfileId":"qwen3_5","LoadParams":{"model":"QwenAlias"}}'
    );
    expect(parsed).toEqual({
      routerModelId: 'QwenAlias',
      runtimeProfileId: 'qwen3_5',
      loadParams: { model: 'QwenAlias' },
    });
  });
});
