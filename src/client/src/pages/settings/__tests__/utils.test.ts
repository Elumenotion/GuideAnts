import { describe, expect, it } from 'vitest';
import type {
  SettingsModelDto,
  SettingsProviderDefinitionDto,
  SettingsReadinessDto,
  SettingsSchemaDto,
  SettingsSectionDto,
  SettingsSectionSchemaDto,
} from '../../../types/settings';
import {
  SECRET_MASK,
  buildAddModelRequest,
  buildCatalogEditRequest,
  buildProfileCreateRequest,
  buildProfileUpdateRequest,
  clonePayload,
  createCatalogEditStateFromModel,
  createEmptyAddModelWizardState,
  createAttachAliasWizardState,
  suggestRuntimeProfileIdForRouterAlias,
  createEmptyProfileForm,
  createProfileFormFromContractShape,
  exportRuntimeProfile,
  formatDateTime,
  getErrorMessage,
  getInputTextValue,
  getProviderDisplayName,
  getSectionSchema,
  getServiceReadiness,
  humanizeKey,
  importRuntimeProfile,
  mapChatProviderToSection,
  parseCanonicalLocalRuntimeJson,
  prepareSectionPayloadForSave,
  parseFieldValue,
  parseRuntimeProfileId,
  payloadSignature,
  withSecretPreserved,
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

  it('builds llama-cpp huggingface request with defaulted target directory', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.llamaInstallSource = 'huggingface';
    state.runtimeProfileId = 'gemma4';
    state.llamaRouterModelId = 'gemma-4-12B-it-qat-GGUF';
    state.llamaHuggingFaceRepository = 'unsloth/gemma-4-12B-it-qat-GGUF';
    state.llamaHuggingFaceResolvedRevision = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef';
    state.llamaHuggingFaceArtifactGroupId = 'single::gemma.gguf';
    state.llamaHuggingFaceModelFiles = ['gemma-4-12B-it-qat-UD-Q4_K_XL.gguf'];
    state.llamaHuggingFaceMmprojFiles = ['mmproj-BF16.gguf'];
    state.llamaHuggingFaceRouterPresetRows = [{ key: 'ctx-size', value: '8192' }];

    const request = buildAddModelRequest(state);

    expect(request.catalog.modelId).toBe('gemma-4-12B-it-qat-GGUF');
    expect(request.catalog.displayName).toBe('gemma-4-12B-it-qat-GGUF');
    expect(request.install?.huggingFace?.targetDirectory).toBe('gemma-4-12B-it-qat-GGUF');
    expect(request.install?.huggingFace?.modelFiles).toEqual(['gemma-4-12B-it-qat-UD-Q4_K_XL.gguf']);
  });

  it('builds llama-cpp huggingface request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'qwen3.5-local';
    state.catalogDisplayName = 'Qwen3.5 Local';
    state.llamaInstallSource = 'huggingface';
    state.runtimeProfileId = 'qwen3_5';
    state.llamaRouterModelId = 'Qwen3.5-9B-Q5_K_M';
    state.llamaHuggingFaceRepository = 'unsloth/Qwen3.5-9B-GGUF';
    state.llamaHuggingFaceResolvedRevision = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef';
    state.llamaHuggingFaceArtifactGroupId = 'single::Qwen3.5-9B-Q5_K_M.gguf';
    state.llamaHuggingFaceModelFiles = ['Qwen3.5-9B-Q5_K_M.gguf'];
    state.llamaHuggingFaceTargetDirectory = 'Qwen3.5-9B-Q5_K_M';
    state.llamaHuggingFaceRouterPresetRows = [{ key: 'ctx-size', value: '8192' }];

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('llama-cpp');
    expect(request.providerConfig).toEqual({ onboardingUi: 'settings' });
    expect(request.install?.source).toBe('huggingface');
    expect(request.install?.huggingFace?.repository).toBe('unsloth/Qwen3.5-9B-GGUF');
    expect(request.install?.routerContextSize).toBeUndefined();
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
  it('preserves existing runtimeConfigJson and model-owned behavior for llama-cpp edits', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'qwen-local',
        provider: 'llama-cpp',
        displayName: 'Qwen Local',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: '',
      },
      {
        runtimeConfigJson: '{"routerModelId":"QwenAlias"}',
        preserveModelBehavior: {
          combineSystemAndDeveloperMessages: true,
          thoughtBlockPattern: '',
          samplingParametersJson: '{"temperature":0.7}',
          thinkingControlJson: '{"defaultChoice":"none","choiceActions":{"none":[]}}',
          requestFieldsWhenToolsPresentJson: '{"parallel_tool_calls":false}',
          reasoningChoicesJson: '["none"]',
        },
      },
    );

    expect(request.modelId).toBe('qwen-local');
    expect(request.runtimeConfigJson).toBe('{"routerModelId":"QwenAlias"}');
    expect(request.samplingParametersJson).toBe('{"temperature":0.7}');
    expect(request.reasoningChoicesJson).toBe('["none"]');
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
      requestFieldsWhenToolsPresentJson: '{}',
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

describe('profile form helpers', () => {
  it('builds create and update requests from valid profile form state', () => {
    const form = {
      ...createEmptyProfileForm(),
      profileId: 'qwen3_5',
      displayName: 'Qwen 3.5',
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
    };

    const created = buildProfileCreateRequest(form);
    expect(created.profileId).toBe('qwen3_5');
    expect(buildProfileUpdateRequest(form)).toEqual(created);
  });

  it('rejects invalid profile ids and malformed JSON fields', () => {
    expect(() =>
      buildProfileCreateRequest({
        ...createEmptyProfileForm(),
        profileId: 'Bad-ID',
        displayName: 'Bad',
        samplingParametersJson: '{}',
        thinkingControlJson: '{}',
      })
    ).toThrow(/lowercase letter/);

    expect(() =>
      buildProfileCreateRequest({
        ...createEmptyProfileForm(),
        profileId: 'valid_id',
        displayName: 'Valid',
        samplingParametersJson: '{bad',
        thinkingControlJson: '{}',
      })
    ).toThrow();
  });

  it('maps contract shape into editable form state', () => {
    const form = createProfileFormFromContractShape({
      profileId: 'openai_default',
      displayName: 'OpenAI Default',
      description: 'desc',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      providers: ['openai-chat'],
    });

    expect(form.profileId).toBe('openai_default');
    expect(form.thoughtBlockPattern).toBe('');
  });
});

describe('catalog edit helpers', () => {
  it('creates catalog edit state from a persisted model dto', () => {
    const model: SettingsModelDto = {
      modelId: 'qwen-local',
      provider: 'llama-cpp',
      displayName: 'Qwen Local',
      description: 'local model',
      displayOrder: 3,
      isActive: true,
      runtimeConfigJson: JSON.stringify({
        routerModelId: 'QwenAlias',
        runtimeProfileId: 'qwen3_5',
        parallelToolCalls: true,
        routerContextSize: 8192,
      }),
    };

    expect(createCatalogEditStateFromModel(model)).toMatchObject({
      modelId: 'qwen-local',
      displayName: 'Qwen Local',
      runtimeProfileId: '',
      displayOrder: '3',
    });
  });

  it('preserves model-owned reasoning choices when editing llama-cpp catalog rows', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'qwen-local',
        provider: 'llama-cpp',
        displayName: 'Qwen Local',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: '',
      },
      {
        preserveModelBehavior: {
          combineSystemAndDeveloperMessages: true,
          thoughtBlockPattern: '',
          samplingParametersJson: '{}',
          thinkingControlJson: '{"choiceActions":{"low":[],"high":[]}}',
          requestFieldsWhenToolsPresentJson: '{}',
          reasoningChoicesJson: '["low","high"]',
        },
      },
    );

    expect(request.reasoningChoicesJson).toBe('["low","high"]');
  });
});

describe('settings utility helpers', () => {
  it('humanizes keys and formats timestamps', () => {
    expect(humanizeKey('routerModelId')).toBe('router Model Id');
    expect(humanizeKey('some_value-name')).toBe('some value name');
    expect(formatDateTime('not-a-date')).toBe('not-a-date');
    expect(formatDateTime(undefined)).toBe('Unknown');
  });

  it('extracts error messages from API-shaped errors', () => {
    expect(getErrorMessage({ body: { errors: ['first', 'second'] } }, 'fallback')).toBe('first second');
    expect(getErrorMessage({ body: { error: 'denied' } }, 'fallback')).toBe('denied');
    expect(getErrorMessage({ message: 'boom' }, 'fallback')).toBe('boom');
    expect(getErrorMessage(null, 'fallback')).toBe('fallback');
  });

  it('normalizes payload signatures and clones', () => {
    const signature = payloadSignature({ b: 2, a: 1 });
    expect(signature).toBe('{"a":1,"b":2}');
    expect(clonePayload({ nested: { value: true } })).toEqual({ nested: { value: true } });
  });

  it('reads schema sections, readiness, and provider labels', () => {
    const schema: SettingsSchemaDto = {
      sections: [{ sectionName: 'OpenAI', properties: [] }],
    };
    const readiness: SettingsReadinessDto = {
      services: [{ serviceId: 'Embeddings', status: 'ready', blockers: [], warnings: [] }],
    };
    const providers: SettingsProviderDefinitionDto[] = [
      { providerId: 'Embeddings.Local.Emb', providerKind: 'Local', providerSection: 'LocalEmb' },
    ];

    expect(getSectionSchema(schema, 'OpenAI')?.sectionName).toBe('OpenAI');
    expect(getSectionSchema(null, 'OpenAI')).toBeUndefined();
    expect(getServiceReadiness(null, 'Embeddings')).toBeNull();
    expect(getServiceReadiness(readiness, 'Embeddings')?.serviceId).toBe('Embeddings');
    expect(getProviderDisplayName(providers, 'Embeddings.Local.Emb')).toContain('Emb');
  });

  it('parses field values and maps chat providers to settings sections', () => {
    expect(getInputTextValue(null)).toBe('');
    expect(getInputTextValue(42)).toBe('42');
    expect(parseFieldValue('', { name: 'Count', valueType: 'int', required: false, enumOptions: null, operative: true })).toBeNull();
    expect(
      parseFieldValue('12', { name: 'Count', valueType: 'int', required: false, enumOptions: null, operative: true })
    ).toBe(12);

    expect(mapChatProviderToSection('openai-chat')).toBe('OpenAI');
    expect(mapChatProviderToSection('llama-cpp')).toBe('LlamaCpp');
    expect(mapChatProviderToSection('unknown-provider')).toBeNull();
  });

  it('exports the secret mask constant', () => {
    expect(SECRET_MASK).toBe('********');
  });

  it('withSecretPreserved sends mask when field is empty but secret is stored', () => {
    expect(withSecretPreserved('', true)).toBe(SECRET_MASK);
    expect(withSecretPreserved('   ', true)).toBe(SECRET_MASK);
    expect(withSecretPreserved('hf_new_token', true)).toBe('hf_new_token');
    expect(withSecretPreserved('', false)).toBe('');
  });

  it('prepareSectionPayloadForSave preserves stored secrets omitted from draft', () => {
    const section: SettingsSectionDto = {
      sectionName: 'HuggingFace',
      schemaVersion: 1,
      rowVersion: 'rv',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { Token: SECRET_MASK, RouterBaseUrl: 'https://router.huggingface.co/v1' },
      secretHasValue: { Token: true },
    };
    const schema: SettingsSectionSchemaDto = {
      sectionName: 'HuggingFace',
      schemaVersion: 1,
      hasSecrets: true,
      properties: [
        { name: 'Token', valueType: 'string', isSecret: true, isEditable: true, isRequired: true },
        { name: 'RouterBaseUrl', valueType: 'string', isSecret: false, isEditable: true, isRequired: false },
      ],
    };

    const payload = prepareSectionPayloadForSave(
      { RouterBaseUrl: 'https://example.com' },
      section,
      schema,
    );

    expect(payload.Token).toBe(SECRET_MASK);
    expect(payload.RouterBaseUrl).toBe('https://example.com');
  });

  it('formats valid ISO timestamps', () => {
    const formatted = formatDateTime('2026-01-15T12:00:00.000Z');
    expect(formatted).not.toBe('Unknown');
    expect(formatted).not.toBe('2026-01-15T12:00:00.000Z');
  });

  it('falls back when error message is blank', () => {
    expect(getErrorMessage({ message: '   ' }, 'fallback')).toBe('fallback');
  });

  it('returns raw string for non-int parseFieldValue and NaN int input', () => {
    expect(
      parseFieldValue('hello', { name: 'Label', valueType: 'string', required: false, enumOptions: null, operative: true })
    ).toBe('hello');
    expect(
      parseFieldValue('abc', { name: 'Count', valueType: 'int', required: false, enumOptions: null, operative: true })
    ).toBe('abc');
  });

  it('maps all known chat providers to settings sections', () => {
    expect(mapChatProviderToSection('openai-responses')).toBe('OpenAI');
    expect(mapChatProviderToSection('azure-openai-chat')).toBe('AzureOpenAI');
    expect(mapChatProviderToSection('azure-openai-responses')).toBe('AzureOpenAI');
    expect(mapChatProviderToSection('anthropic')).toBe('Anthropic');
    expect(mapChatProviderToSection('google-gemini-chat')).toBe('GoogleGeminiApi');
    expect(mapChatProviderToSection('hf-inference-chat')).toBe('HuggingFace');
    expect(mapChatProviderToSection('openrouter-chat')).toBe('OpenRouter');
    expect(mapChatProviderToSection('  LLAMA-CPP  ')).toBe('LlamaCpp');
  });
});

describe('importRuntimeProfile validation', () => {
  const validProfile = {
    profileId: 'openai_default',
    displayName: 'OpenAI Default',
    combineSystemAndDeveloperMessages: false,
    samplingParametersJson: '{}',
    thinkingControlJson: '{}',
  };

  it('rejects invalid json and non-object payloads', () => {
    expect(() => importRuntimeProfile('{')).toThrow('valid JSON');
    expect(() => importRuntimeProfile('[]')).toThrow('single JSON object');
    expect(() => importRuntimeProfile('null')).toThrow('single JSON object');
  });

  it('rejects missing or invalid contract fields', () => {
    expect(() => importRuntimeProfile(JSON.stringify({ ...validProfile, profileId: '' }))).toThrow('profileId');
    expect(() => importRuntimeProfile(JSON.stringify({ ...validProfile, displayName: '' }))).toThrow('displayName');
    expect(() =>
      importRuntimeProfile(JSON.stringify({ ...validProfile, combineSystemAndDeveloperMessages: 'yes' }))
    ).toThrow('combineSystemAndDeveloperMessages');
    expect(() =>
      importRuntimeProfile(JSON.stringify({ ...validProfile, description: 42 }))
    ).toThrow('description');
    expect(() =>
      importRuntimeProfile(JSON.stringify({ ...validProfile, thoughtBlockPattern: 99 }))
    ).toThrow('thoughtBlockPattern');
    expect(() =>
      importRuntimeProfile(JSON.stringify({ ...validProfile, thinkingControlJson: 42 }))
    ).toThrow('thinkingControlJson');
    expect(() =>
      importRuntimeProfile(JSON.stringify({ ...validProfile, samplingParametersJson: 42 }))
    ).toThrow('samplingParametersJson');
  });
});

describe('getInputTextValue', () => {
  it('coerces nullish and non-string values to text', () => {
    expect(getInputTextValue(null)).toBe('');
    expect(getInputTextValue(undefined)).toBe('');
    expect(getInputTextValue('hello')).toBe('hello');
    expect(getInputTextValue(42)).toBe('42');
  });
});

describe('parseRuntimeProfileId and parseCanonicalLocalRuntimeJson', () => {
  it('reads runtime profile id from mixed-case keys', () => {
    expect(parseRuntimeProfileId('{"runtimeprofileid":"local_default"}')).toBe('local_default');
    expect(parseRuntimeProfileId('{"RuntimeProfileId":"pascal_case"}')).toBe('pascal_case');
    expect(parseRuntimeProfileId('not-json')).toBe('');
    expect(parseRuntimeProfileId(undefined)).toBe('');
  });

  it('returns null for invalid canonical local runtime json', () => {
    expect(parseCanonicalLocalRuntimeJson('')).toBeNull();
    expect(parseCanonicalLocalRuntimeJson('[]')).toBeNull();
    expect(parseCanonicalLocalRuntimeJson('{"runtimeProfileId":"qwen3_5"}')).toBeNull();
    expect(parseCanonicalLocalRuntimeJson('bad-json')).toBeNull();
  });

  it('accepts router-only canonical local runtime json', () => {
    expect(parseCanonicalLocalRuntimeJson('{"routerModelId":"a"}')).toEqual({
      routerModelId: 'a',
    });
  });

  it('parses optional router cache and parallel tool call fields', () => {
    expect(
      parseCanonicalLocalRuntimeJson(
        JSON.stringify({
          routerModelId: 'Qwen',
          runtimeProfileId: 'qwen3_5',
          parallelToolCalls: true,
          routerCacheRamMib: 256,
        })
      )
    ).toEqual({
      routerModelId: 'Qwen',
      runtimeProfileId: 'qwen3_5',
      parallelToolCalls: true,
      routerCacheRamMib: 256,
    });
  });
});

describe('buildAddModelRequest validation', () => {
  it('rejects incomplete wizard state', () => {
    expect(() => buildAddModelRequest(createEmptyAddModelWizardState())).toThrow('Pick a provider');
    const missingModel = createEmptyAddModelWizardState('openai-chat');
    missingModel.catalogDisplayName = 'Name';
    expect(() => buildAddModelRequest(missingModel)).toThrow('Catalog Model ID');
    const missingName = createEmptyAddModelWizardState('openai-chat');
    missingName.catalogModelId = 'gpt-4o';
    expect(() => buildAddModelRequest(missingName)).toThrow('display name');
  });

  it('rejects non-integer display order', () => {
    const state = createEmptyAddModelWizardState('openai-chat');
    state.catalogModelId = 'gpt-4o';
    state.catalogDisplayName = 'GPT-4o';
    state.catalogDisplayOrder = '1.5';
    expect(() => buildAddModelRequest(state)).toThrow('whole number');
  });

  it('trims preselected provider when creating empty wizard state', () => {
    expect(createEmptyAddModelWizardState('  anthropic  ').provider).toBe('anthropic');
  });

  it('surfaces onboarding validation errors for incomplete llama-cpp installs', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'qwen-local';
    state.catalogDisplayName = 'Qwen Local';
    state.llamaInstallSource = 'huggingface';
    expect(() => buildAddModelRequest(state)).toThrow();
  });

  it('prefills attach-alias wizard state from router alias', () => {
    expect(suggestRuntimeProfileIdForRouterAlias('Qwen3.5-9B-GGUF')).toBe('qwen3_5');
    const state = createAttachAliasWizardState('Qwen3.5-9B-GGUF');
    expect(state.llamaInstallSource).toBe('existingAlias');
    expect(state.llamaExistingAliasRouterModelId).toBe('Qwen3.5-9B-GGUF');
    expect(state.catalogModelId).toBe('Qwen3.5-9B-GGUF');
    expect(state.runtimeProfileId).toBe('qwen3_5');
  });
});

describe('buildCatalogEditRequest edge cases', () => {
  it('builds non-llama runtime config from runtime profile id only', () => {
    const request = buildCatalogEditRequest({
      modelId: 'gpt-4o',
      provider: 'openai-chat',
      displayName: 'GPT-4o',
      description: '  optional desc  ',
      displayOrder: '2',
      isActive: true,
      runtimeProfileId: 'openai_default',
    });

    expect(request.runtimeConfigJson).toBe(JSON.stringify({ runtimeProfileId: 'openai_default' }));
    expect(request.description).toBe('optional desc');
    expect(request.displayOrder).toBe(2);
    expect(request.reasoningChoicesJson).toBeUndefined();
  });

  it('rejects missing model id, provider, and display name', () => {
    const base = {
      modelId: 'qwen-local',
      provider: 'llama-cpp',
      displayName: 'Qwen',
      description: '',
      displayOrder: '',
      isActive: true,
      runtimeProfileId: 'qwen3_5',
    };

    expect(() => buildCatalogEditRequest({ ...base, modelId: '  ' })).toThrow('Model ID is required');
    expect(() => buildCatalogEditRequest({ ...base, provider: '' })).toThrow('Provider is required');
    expect(() => buildCatalogEditRequest({ ...base, displayName: ' ' })).toThrow('Display name is required');
  });

  it('preserves llama-cpp runtime config from options without deriving from form fields', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'qwen-local',
        provider: 'llama-cpp',
        displayName: 'Qwen',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: '',
      },
      { runtimeConfigJson: '{"routerModelId":"QwenAlias"}' }
    );
    expect(request.runtimeConfigJson).toBe('{"routerModelId":"QwenAlias"}');
  });

  it('omits reasoning choices when thinking control json has no choice actions object', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'gpt-4.1',
        provider: 'openai-chat',
        displayName: 'GPT-4.1',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: 'openai_default',
      },
      { profileThinkingControlJson: '{}' }
    );
    expect(request.reasoningChoicesJson).toBeUndefined();
  });

  it('ignores malformed profile thinking control json when deriving reasoning choices', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'gpt-4.1',
        provider: 'openai-chat',
        displayName: 'GPT-4.1',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: 'openai_default',
      },
      { profileThinkingControlJson: '{' }
    );
    expect(request.reasoningChoicesJson).toBeUndefined();
  });

  it('omits reasoning choices when choice actions are empty', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'gpt-4.1',
        provider: 'openai-chat',
        displayName: 'GPT-4.1',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: 'openai_default',
      },
      { profileThinkingControlJson: '{"choiceActions":{"  ":[]}}' }
    );
    expect(request.reasoningChoicesJson).toBeUndefined();
  });

  it('omits reasoning choices when profile thinking control is invalid', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'gpt-4.1',
        provider: 'openai-chat',
        displayName: 'GPT-4.1',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: 'openai_default',
      },
      { profileThinkingControlJson: '{bad-json' }
    );
    expect(request.reasoningChoicesJson).toBeUndefined();
  });
});

describe('profile form create request edge cases', () => {
  it('rejects empty profile id and display name', () => {
    expect(() =>
      buildProfileCreateRequest({
        ...createEmptyProfileForm(),
        profileId: '   ',
        displayName: 'Name',
        samplingParametersJson: '{}',
        thinkingControlJson: '{}',
      })
    ).toThrow('Profile ID is required');

    expect(() =>
      buildProfileCreateRequest({
        ...createEmptyProfileForm(),
        profileId: 'valid_id',
        displayName: '   ',
        samplingParametersJson: '{}',
        thinkingControlJson: '{}',
      })
    ).toThrow('Display name is required');
  });

  it('trims optional profile fields on create', () => {
    const created = buildProfileCreateRequest({
      ...createEmptyProfileForm(),
      profileId: 'openai_default',
      displayName: 'OpenAI Default',
      description: '  notes  ',
      thoughtBlockPattern: '  pattern  ',
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
    });
    expect(created.description).toBe('notes');
    expect(created.thoughtBlockPattern).toBe('pattern');
  });
});

describe('parseRuntimeProfileId edge cases', () => {
  it('returns empty string when runtime profile id key is not a string', () => {
    expect(parseRuntimeProfileId('{"runtimeprofileid":42}')).toBe('');
  });
});

describe('buildAddModelRequest llama-cpp validation', () => {
  it('surfaces onboarding validation errors for incomplete llama-cpp wizard state', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'qwen-local';
    state.catalogDisplayName = 'Qwen Local';
    state.llamaInstallSource = 'huggingface';
    expect(() => buildAddModelRequest(state)).toThrow();
  });
});

describe('buildCatalogEditRequest reasoning derivation', () => {
  it('omits reasoning choices when profile thinking control has no choiceActions object', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'qwen-local',
        provider: 'llama-cpp',
        displayName: 'Qwen Local',
        description: '',
        displayOrder: '',
        isActive: true,
        runtimeProfileId: 'qwen3_5',
      },
      { profileThinkingControlJson: '{"defaultChoice":"medium"}' }
    );
    expect(request.reasoningChoicesJson).toBeUndefined();
  });
});
