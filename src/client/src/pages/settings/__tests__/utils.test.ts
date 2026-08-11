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
  clonePayload,
  createCatalogEditStateFromModel,
  createEmptyAddModelWizardState,
  createAttachAliasWizardState,
  formatDateTime,
  getErrorMessage,
  getInputTextValue,
  getProviderDisplayName,
  getSectionSchema,
  getServiceReadiness,
  humanizeKey,
  mapChatProviderToSection,
  parseCanonicalLocalRuntimeJson,
  prepareSectionPayloadForSave,
  parseFieldValue,
  payloadSignature,
  stripStoredSecretPlaceholders,
  withSecretPreserved,
} from '../utils';

describe('buildAddModelRequest', () => {
  it('builds openai-chat request with row-owned parameter surface', () => {
    const state = createEmptyAddModelWizardState('openai-chat');
    state.catalogModelId = 'gpt-4o-chat';
    state.catalogDisplayName = 'GPT-4o Chat';
    state.samplingParametersJson = '{"temperature":{"key":"temperature","label":"Temperature","description":"","min":0,"max":2,"step":0.1,"default":1,"displayOrder":0,"exposedInGuideBuilder":true}}';
    state.reasoningChoicesJson = '';

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('openai-chat');
    expect(request.providerConfig).toEqual({
      samplingParametersJson: state.samplingParametersJson,
    });
    expect(request.install).toBeUndefined();
  });

  it('sends row-owned request shaping for openrouter and omits it elsewhere', () => {
    const openRouter = createEmptyAddModelWizardState('openrouter-chat');
    openRouter.catalogModelId = 'qwen/qwen3-32b';
    openRouter.catalogDisplayName = 'Qwen3 32B';
    openRouter.thinkingControlJson =
      '{"defaultChoice":"none","choiceActions":{"none":[{"target":"NestedRequestField","key":"chat_template_kwargs.enable_thinking","value":false}]}}';
    openRouter.requestFieldsWhenToolsPresentJson = '{"parallel_tool_calls":false}';

    const openRouterRequest = buildAddModelRequest(openRouter);
    expect(openRouterRequest.providerConfig).toMatchObject({
      thinkingControlJson: openRouter.thinkingControlJson,
      requestFieldsWhenToolsPresentJson: '{"parallel_tool_calls":false}',
    });

    const openAi = createEmptyAddModelWizardState('openai-chat');
    openAi.catalogModelId = 'gpt-4o-chat';
    openAi.catalogDisplayName = 'GPT-4o Chat';
    openAi.thinkingControlJson = '{"defaultChoice":"none","choiceActions":{}}';

    const openAiRequest = buildAddModelRequest(openAi);
    expect(openAiRequest.providerConfig).not.toHaveProperty('thinkingControlJson');
  });

  it('rejects malformed behavior json before sending an add-model request', () => {
    const state = createEmptyAddModelWizardState('hf-inference-chat');
    state.catalogModelId = 'Qwen/Qwen3-32B';
    state.catalogDisplayName = 'Qwen3 32B';
    state.requestFieldsWhenToolsPresentJson = '{not json';

    expect(() => buildAddModelRequest(state)).toThrow('Extra request fields JSON must be valid JSON.');
  });

  it('defaults anthropic add-model state with expected empty fields', () => {
    const state = createEmptyAddModelWizardState('anthropic');

    expect(state.provider).toBe('anthropic');
    expect(state.catalogIsActive).toBe(true);
  });

  it('builds llama-cpp huggingface request with defaulted target directory', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.llamaInstallSource = 'huggingface';
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
    expect(request.install).not.toHaveProperty('runtimeProfileId');
    expect(request.providerConfig).toEqual({
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      onboardingUi: 'settings',
    });
  });

  it('builds llama-cpp huggingface request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'qwen3.5-local';
    state.catalogDisplayName = 'Qwen3.5 Local';
    state.llamaInstallSource = 'huggingface';
    state.llamaRouterModelId = 'Qwen3.5-9B-Q5_K_M';
    state.llamaHuggingFaceRepository = 'unsloth/Qwen3.5-9B-GGUF';
    state.llamaHuggingFaceResolvedRevision = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef';
    state.llamaHuggingFaceArtifactGroupId = 'single::Qwen3.5-9B-Q5_K_M.gguf';
    state.llamaHuggingFaceModelFiles = ['Qwen3.5-9B-Q5_K_M.gguf'];
    state.llamaHuggingFaceTargetDirectory = 'Qwen3.5-9B-Q5_K_M';
    state.llamaHuggingFaceRouterPresetRows = [{ key: 'ctx-size', value: '8192' }];

    const request = buildAddModelRequest(state);

    expect(request.provider).toBe('llama-cpp');
    expect(request.providerConfig).toEqual({
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      onboardingUi: 'settings',
    });
    expect(request.install?.source).toBe('huggingface');
    expect(request.install?.huggingFace?.repository).toBe('unsloth/Qwen3.5-9B-GGUF');
    expect(request.install?.routerContextSize).toBeUndefined();
    expect(request.install).not.toHaveProperty('runtimeProfileId');
  });

  it('builds llama-cpp existingAlias request', () => {
    const state = createEmptyAddModelWizardState('llama-cpp');
    state.catalogModelId = 'adopted-alias-model';
    state.catalogDisplayName = 'Adopted Alias Model';
    state.llamaInstallSource = 'existingAlias';
    state.llamaExistingAliasRouterModelId = 'Qwen3.5-9B-Q5_K_M';

    const request = buildAddModelRequest(state);

    expect(request.install?.source).toBe('existingAlias');
    expect(request.install?.routerModelId).toBe('Qwen3.5-9B-Q5_K_M');
    expect(request.install).not.toHaveProperty('runtimeProfileId');
    expect(request.providerConfig).toEqual({
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      onboardingUi: 'settings',
    });
  });
});

describe('buildCatalogEditRequest', () => {
  it('preserves existing runtimeConfigJson and persists row-owned behavior for llama-cpp edits', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'qwen-local',
        provider: 'llama-cpp',
        displayName: 'Qwen Local',
        description: '',
        displayOrder: '',
        isActive: true,
        samplingParametersJson: '{"temperature":0.2}',
        reasoningChoicesJson: '["low"]',
        thinkingControlJson: '{"defaultChoice":"low","choiceActions":{"low":[]}}',
        requestFieldsWhenToolsPresentJson: '{"parallel_tool_calls":true}',
        combineSystemAndDeveloperMessages: false,
        thoughtBlockPattern: '<thought>(.*?)</thought>',
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
    expect(request.samplingParametersJson).toBe('{"temperature":0.2}');
    expect(request.reasoningChoicesJson).toBe('["low"]');
    expect(request.thinkingControlJson).toBe('{"defaultChoice":"low","choiceActions":{"low":[]}}');
    expect(request.requestFieldsWhenToolsPresentJson).toBe('{"parallel_tool_calls":true}');
    expect(request.combineSystemAndDeveloperMessages).toBe(false);
    expect(request.thoughtBlockPattern).toBe('<thought>(.*?)</thought>');
  });
});

describe('legacy runtime config parsers', () => {
  it('imports canonical local runtime config from legacy PascalCase keys', () => {
    const parsed = parseCanonicalLocalRuntimeJson(
      '{"RouterModelId":"QwenAlias","RuntimeProfileId":"qwen3_5","LoadParams":{"model":"QwenAlias"}}'
    );
    expect(parsed).toEqual({
      routerModelId: 'QwenAlias',
      loadParams: { model: 'QwenAlias' },
    });
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
        parallelToolCalls: true,
        routerContextSize: 8192,
      }),
    };

    expect(createCatalogEditStateFromModel(model)).toMatchObject({
      modelId: 'qwen-local',
      displayName: 'Qwen Local',
      samplingParametersJson: '{}',
      reasoningChoicesJson: '',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
      displayOrder: '3',
    });
  });

  it('persists state-owned reasoning choices when editing llama-cpp catalog rows', () => {
    const request = buildCatalogEditRequest(
      {
        modelId: 'qwen-local',
        provider: 'llama-cpp',
        displayName: 'Qwen Local',
        description: '',
        displayOrder: '',
        isActive: true,
        samplingParametersJson: '{}',
        reasoningChoicesJson: '["medium"]',
        thinkingControlJson: '{"choiceActions":{"medium":[]}}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: false,
        thoughtBlockPattern: '',
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

    expect(request.reasoningChoicesJson).toBe('["medium"]');
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

  it('prepareSectionPayloadForSave omits a stored secret left blank in the draft', () => {
    // Regression for the Microsoft Foundry Connections bug: an untouched secret field must be
    // dropped from the save payload entirely (not sent as SECRET_MASK), so a value a password
    // manager silently autofilled into the field is never sent unless the input is non-blank.
    const section: SettingsSectionDto = {
      sectionName: 'HuggingFace',
      schemaVersion: 1,
      rowVersion: 'rv',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { Token: '', RouterBaseUrl: 'https://router.huggingface.co/v1' },
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
      { Token: '', RouterBaseUrl: 'https://example.com' },
      section,
      schema,
    );

    expect('Token' in payload).toBe(false);
    expect(payload.RouterBaseUrl).toBe('https://example.com');
  });

  it('prepareSectionPayloadForSave sends a non-blank secret as typed', () => {
    const section: SettingsSectionDto = {
      sectionName: 'HuggingFace',
      schemaVersion: 1,
      rowVersion: 'rv',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { Token: '', RouterBaseUrl: 'https://router.huggingface.co/v1' },
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
      { Token: 'hf_new_token', RouterBaseUrl: 'https://router.huggingface.co/v1' },
      section,
      schema,
    );

    expect(payload.Token).toBe('hf_new_token');
  });

  it('prepareSectionPayloadForSave keeps sending empty string when no secret is stored yet', () => {
    const section: SettingsSectionDto = {
      sectionName: 'HuggingFace',
      schemaVersion: 1,
      rowVersion: 'rv',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { Token: '', RouterBaseUrl: '' },
      secretHasValue: { Token: false },
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

    const payload = prepareSectionPayloadForSave({ Token: '', RouterBaseUrl: '' }, section, schema);

    // No stored secret to preserve - sending '' still lets server-side required-field
    // validation reject the save, same as before this change.
    expect(payload.Token).toBe('');
  });

  it('prepareSectionPayloadForSave falls back to section.secretHasValue when schema failed to load', () => {
    // Guards the hazard noted in the investigation: if getSchema() errored, schema is
    // undefined - the guard must still find the secret fields instead of skipping the
    // omit-on-blank normalization entirely.
    const section: SettingsSectionDto = {
      sectionName: 'AzureOpenAI',
      schemaVersion: 1,
      rowVersion: 'rv',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { Resource: 'my-foundry', ApiKey: '' },
      secretHasValue: { ApiKey: true },
    };

    const payload = prepareSectionPayloadForSave(
      { Resource: 'new-foundry-resource', ApiKey: '' },
      section,
      undefined,
    );

    expect('ApiKey' in payload).toBe(false);
    expect(payload.Resource).toBe('new-foundry-resource');
  });

  it('stripStoredSecretPlaceholders blanks stored secrets and leaves other fields untouched', () => {
    const section: SettingsSectionDto = {
      sectionName: 'AzureOpenAI',
      schemaVersion: 1,
      rowVersion: 'rv',
      updatedUtc: '2026-01-01T00:00:00Z',
      payload: { Resource: 'my-foundry', ApiKey: SECRET_MASK, ApiVersion: '2025-04-01-preview' },
      secretHasValue: { ApiKey: true },
    };

    const draft = stripStoredSecretPlaceholders(section);

    expect(draft.ApiKey).toBe('');
    expect(draft.Resource).toBe('my-foundry');
    expect(draft.ApiVersion).toBe('2025-04-01-preview');
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

describe('getInputTextValue', () => {
  it('coerces nullish and non-string values to text', () => {
    expect(getInputTextValue(null)).toBe('');
    expect(getInputTextValue(undefined)).toBe('');
    expect(getInputTextValue('hello')).toBe('hello');
    expect(getInputTextValue(42)).toBe('42');
  });
});

describe('parseCanonicalLocalRuntimeJson', () => {
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
          parallelToolCalls: true,
          routerCacheRamMib: 256,
        })
      )
    ).toEqual({
      routerModelId: 'Qwen',
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
    const state = createAttachAliasWizardState('Qwen3.5-9B-GGUF');
    expect(state.llamaInstallSource).toBe('existingAlias');
    expect(state.llamaExistingAliasRouterModelId).toBe('Qwen3.5-9B-GGUF');
    expect(state.catalogModelId).toBe('Qwen3.5-9B-GGUF');
  });
});

describe('buildCatalogEditRequest edge cases', () => {
  it('builds non-llama catalog edit request from row-owned parameter surface', () => {
    const request = buildCatalogEditRequest({
      modelId: 'gpt-4o',
      provider: 'openai-chat',
      displayName: 'GPT-4o',
      description: '  optional desc  ',
      displayOrder: '2',
      isActive: true,
      samplingParametersJson: '{"temperature":{"key":"temperature","label":"Temperature","description":"","min":0,"max":2,"step":0.1,"default":1,"displayOrder":0,"exposedInGuideBuilder":true}}',
      reasoningChoicesJson: '["low","high"]',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
    });

    expect(request.runtimeConfigJson).toBeUndefined();
    expect(request.samplingParametersJson).toContain('temperature');
    expect(request.reasoningChoicesJson).toBe('["low","high"]');
    expect(request.description).toBe('optional desc');
    expect(request.displayOrder).toBe(2);
  });

  it('rejects missing model id, provider, and display name', () => {
    const base = {
      modelId: 'qwen-local',
      provider: 'llama-cpp',
      displayName: 'Qwen',
      description: '',
      displayOrder: '',
      isActive: true,
      samplingParametersJson: '{}',
      reasoningChoicesJson: '',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
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
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
      },
      { runtimeConfigJson: '{"routerModelId":"QwenAlias"}' }
    );
    expect(request.runtimeConfigJson).toBe('{"routerModelId":"QwenAlias"}');
  });

  it('normalizes empty non-llama reasoning choices to undefined', () => {
    const request = buildCatalogEditRequest({
      modelId: 'gpt-4.1',
      provider: 'openai-chat',
      displayName: 'GPT-4.1',
      description: '',
      displayOrder: '',
      isActive: true,
      samplingParametersJson: '{}',
      reasoningChoicesJson: '',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
    });
    expect(request.reasoningChoicesJson).toBeUndefined();
  });

  it('rejects invalid non-llama sampling parameters json', () => {
    expect(() =>
      buildCatalogEditRequest({
        modelId: 'gpt-4.1',
        provider: 'openai-chat',
        displayName: 'GPT-4.1',
        description: '',
        displayOrder: '',
        isActive: true,
        samplingParametersJson: '{bad-json',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
      })
    ).toThrow('valid JSON');
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

describe('buildCatalogEditRequest llama-cpp reasoning', () => {
  it('omits empty llama reasoning choices from row state', () => {
    const request = buildCatalogEditRequest({
      modelId: 'qwen-local',
      provider: 'llama-cpp',
      displayName: 'Qwen Local',
      description: '',
      displayOrder: '',
      isActive: true,
      samplingParametersJson: '{}',
      reasoningChoicesJson: '',
      thinkingControlJson: '{}',
      requestFieldsWhenToolsPresentJson: '{}',
      combineSystemAndDeveloperMessages: true,
      thoughtBlockPattern: '',
    });
    expect(request.reasoningChoicesJson).toBeUndefined();
  });
});
