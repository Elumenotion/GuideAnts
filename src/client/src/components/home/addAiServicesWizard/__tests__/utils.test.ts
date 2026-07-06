import { describe, expect, it } from 'vitest';
import type { ProviderEditorStateDto, ServiceEditorStateDto } from '../../../../types/settings';
import {
  GEMINI_FLASH_RUNTIME_PROFILE_ID,
  GEMINI_PRO_RUNTIME_PROFILE_ID,
  HUGGINGFACE_DEFAULT_RUNTIME_PROFILE_ID,
  OPENAI_CHAT_RUNTIME_PROFILE_ID,
  OPENAI_RESPONSES_RUNTIME_PROFILE_ID,
  OPENROUTER_CHAT_MODEL_PROVIDER_ID,
  OPENROUTER_DEFAULT_RUNTIME_PROFILE_ID,
  OPENROUTER_SECTION,
  OPENROUTER_SERVICE_PROVIDER_IDS,
  LOCAL_AI_SERVICE_PROVIDER_IDS,
  DOCUMENT_INTELLIGENCE_SECTION,
  EMBEDDINGS_SECTION,
  GEMINI_CORE_SECTION,
  GEMINI_SERVICE_PROVIDER_IDS,
  HUGGINGFACE_CHAT_MODEL_PROVIDER_ID,
  HUGGINGFACE_SECTION,
  HUGGINGFACE_SERVICE_PROVIDER_IDS,
  IMAGES_SECTION,
  SERVICE_PROVIDER_IDS,
  SPEECH_SECTION,
} from '../constants';
import {
  deriveEndpointFromResource,
  buildAddGeminiModelRequest,
  buildAddModelRequest,
  buildAddOpenAiModelRequest,
  buildAddHuggingFaceModelRequest,
  buildAddOpenRouterModelRequest,
  hasModelId,
  hasModelTuple,
  mapModelProviderIdToLabel,
  mapProviderLabelToModelProviderId,
  summarizeGeminiOptionalServiceWarnings,
  summarizeHuggingFaceOptionalServiceWarnings,
  summarizeOptionalServiceWarnings,
  summarizeOpenRouterOptionalServiceWarnings,
  summarizeLocalAiOptionalServiceWarnings,
  toExistingLocalModels,
  buildLocalAiModelRequest,
  withSecretPreserved,
  getSchemaDefault,
  isPositiveIntegerValue,
  toExistingHuggingFaceModels,
  toExistingOpenRouterModels,
  toExistingGeminiModels,
  toExistingFoundryModels,
} from '../utils';
import type { OptionalServiceKey, WizardLoadSnapshot } from '../types';

function createProvider(providerId: string, canActivate = true): ProviderEditorStateDto {
  return {
    providerId,
    providerKind: 'Cloud',
    providerSection: 'Test',
    modeId: null,
    hasExplicitMode: true,
    isDefaultMode: true,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate,
    activationBlockers: canActivate ? [] : ['missing'],
    fields: {},
    runtimeDependencies: [],
    operativeFields: [],
    diagnosticFields: [],
    fieldMetadata: [],
  };
}

function createServiceState(serviceId: OptionalServiceKey, providerId: string, canActivate = true): ServiceEditorStateDto {
  return {
    serviceId,
    activeProviderId: providerId,
    providers: [createProvider(providerId, canActivate)],
    readiness: {
      status: canActivate ? 'ready' : 'blocked',
      blockers: canActivate ? [] : ['blocked'],
      warnings: [],
    },
  };
}

function createSnapshot(overrides?: Partial<WizardLoadSnapshot>): WizardLoadSnapshot {
  return {
    sectionSummaries: [
      {
        sectionName: EMBEDDINGS_SECTION,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
      {
        sectionName: IMAGES_SECTION,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
      {
        sectionName: SPEECH_SECTION,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
      {
        sectionName: DOCUMENT_INTELLIGENCE_SECTION,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
    ],
    sectionsByName: {},
    models: [],
    serviceStates: {
      Embeddings: createServiceState('Embeddings', SERVICE_PROVIDER_IDS.Embeddings),
      ImageGeneration: createServiceState('ImageGeneration', SERVICE_PROVIDER_IDS.ImageGeneration),
      SpeechTranscription: createServiceState('SpeechTranscription', SERVICE_PROVIDER_IDS.SpeechTranscription),
      SpeechSynthesis: createServiceState('SpeechSynthesis', SERVICE_PROVIDER_IDS.SpeechSynthesis),
      DocumentIntelligence: createServiceState('DocumentIntelligence', SERVICE_PROVIDER_IDS.DocumentIntelligence),
    },
    defaults: {
      azureOpenAiApiVersion: '2025-04-01-preview',
      azureOpenAiImagesApiVersion: '2025-04-01-preview',
    },
    ...overrides,
  };
}

describe('addAiServicesWizard utils', () => {
  it('maps model provider labels to internal ids and back', () => {
    expect(mapProviderLabelToModelProviderId('Completions')).toBe('azure-openai-chat');
    expect(mapProviderLabelToModelProviderId('Responses')).toBe('azure-openai-responses');
    expect(mapModelProviderIdToLabel('azure-openai-chat')).toBe('Completions');
    expect(mapModelProviderIdToLabel('azure-openai-responses')).toBe('Responses');
    expect(mapModelProviderIdToLabel('openai-chat')).toBeNull();
  });

  it('derives azure endpoint from resource', () => {
    expect(deriveEndpointFromResource('my-resource')).toBe('https://my-resource.openai.azure.com/');
    expect(deriveEndpointFromResource('  my-resource  ')).toBe('https://my-resource.openai.azure.com/');
    expect(deriveEndpointFromResource('')).toBe('');
  });

  it('filters to foundry chat/responses models', () => {
    const models = toExistingFoundryModels([
      {
        modelId: 'gpt-4.1',
        displayName: 'gpt-4.1',
        provider: 'azure-openai-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
      {
        modelId: 'gpt-5',
        displayName: 'gpt-5',
        provider: 'azure-openai-responses',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
      {
        modelId: 'gpt-4o-openai',
        displayName: 'gpt-4o-openai',
        provider: 'openai-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
    ]);

    expect(models).toHaveLength(2);
    expect(models.map((item) => `${item.modelId}:${item.provider}`)).toEqual([
      'gpt-4.1:Completions',
      'gpt-5:Responses',
    ]);
  });

  it('filters to gemini chat models', () => {
    const models = toExistingGeminiModels([
      {
        modelId: 'gemini-2.5-flash',
        displayName: 'gemini-2.5-flash',
        provider: 'google-gemini-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
      {
        modelId: 'gpt-4o',
        displayName: 'gpt-4o',
        provider: 'azure-openai-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
    ]);

    expect(models).toHaveLength(1);
    expect(models[0]?.modelId).toBe('gemini-2.5-flash');
  });

  it('checks model duplication helpers case-insensitively', () => {
    expect(
      hasModelTuple(
        [{ modelId: 'gpt-4.1', provider: 'Completions' }],
        { modelId: 'GPT-4.1', provider: 'Completions' }
      )
    ).toBe(true);
    expect(hasModelTuple([{ modelId: 'gpt-4.1', provider: 'Completions' }], { modelId: 'gpt-4.1', provider: 'Responses' })).toBe(false);
    expect(hasModelId([{ modelId: 'gpt-4.1' }], 'GPT-4.1')).toBe(true);
  });

  it('returns no optional service warnings when all services are configured and active', () => {
    expect(summarizeOptionalServiceWarnings(createSnapshot())).toEqual([]);
  });

  it('includes speech warnings when speech connection is unconfigured', () => {
    const snapshot = createSnapshot({
      sectionSummaries: createSnapshot().sectionSummaries.map((section) =>
        section.sectionName === SPEECH_SECTION
          ? { ...section, readinessStatus: 'unconfigured' }
          : section
      ),
    });

    expect(summarizeOptionalServiceWarnings(snapshot)).toEqual(
      expect.arrayContaining([
        'Speech connection is not configured for transcription.',
        'Speech connection is not configured for synthesis.',
      ])
    );
  });

  it('returns no gemini optional warnings when gemini providers are active and configured', () => {
    const snapshot = createSnapshot({
      sectionSummaries: [
        ...createSnapshot().sectionSummaries,
        {
          sectionName: GEMINI_CORE_SECTION,
          hasSecrets: true,
          readinessStatus: 'configured',
          missingFields: [],
        },
      ],
      serviceStates: {
        Embeddings: createServiceState('Embeddings', GEMINI_SERVICE_PROVIDER_IDS.Embeddings),
        ImageGeneration: createServiceState('ImageGeneration', GEMINI_SERVICE_PROVIDER_IDS.ImageGeneration),
        SpeechTranscription: createServiceState('SpeechTranscription', GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription),
        SpeechSynthesis: createServiceState('SpeechSynthesis', GEMINI_SERVICE_PROVIDER_IDS.SpeechSynthesis),
        DocumentIntelligence: createServiceState('DocumentIntelligence', SERVICE_PROVIDER_IDS.DocumentIntelligence),
      },
    });

    expect(summarizeGeminiOptionalServiceWarnings(snapshot)).toEqual([]);
  });

  it('filters to hf chat models', () => {
    const models = toExistingHuggingFaceModels([
      {
        modelId: 'zai-org/GLM-5.2',
        displayName: 'zai-org/GLM-5.2',
        provider: HUGGINGFACE_CHAT_MODEL_PROVIDER_ID,
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
      {
        modelId: 'gpt-4.1',
        displayName: 'gpt-4.1',
        provider: 'openai-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
    ]);

    expect(models).toHaveLength(1);
    expect(models[0]?.modelId).toBe('zai-org/GLM-5.2');
  });

  it('builds hf add-model request with runtime profile id', () => {
    const request = buildAddHuggingFaceModelRequest('zai-org/GLM-5.2');
    expect(request.provider).toBe(HUGGINGFACE_CHAT_MODEL_PROVIDER_ID);
    expect(request.providerConfig).toEqual({
      runtimeProfileId: HUGGINGFACE_DEFAULT_RUNTIME_PROFILE_ID,
    });
  });

  it('filters to openrouter chat models', () => {
    const models = toExistingOpenRouterModels([
      {
        modelId: 'minimax/minimax-m3',
        displayName: 'minimax/minimax-m3',
        provider: OPENROUTER_CHAT_MODEL_PROVIDER_ID,
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
      {
        modelId: 'gpt-4.1',
        displayName: 'gpt-4.1',
        provider: 'openai-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
    ]);

    expect(models).toHaveLength(1);
    expect(models[0]?.modelId).toBe('minimax/minimax-m3');
  });

  it('builds openrouter add-model request with runtime profile id', () => {
    const request = buildAddOpenRouterModelRequest('minimax/minimax-m3');
    expect(request.provider).toBe(OPENROUTER_CHAT_MODEL_PROVIDER_ID);
    expect(request.providerConfig).toEqual({
      runtimeProfileId: OPENROUTER_DEFAULT_RUNTIME_PROFILE_ID,
    });
  });

  it('builds Foundry add-model requests with provider-specific runtime profile ids', () => {
    expect(buildAddModelRequest('gpt-4o', 'Completions').providerConfig).toEqual({
      runtimeProfileId: OPENAI_CHAT_RUNTIME_PROFILE_ID,
    });
    expect(buildAddModelRequest('gpt-5.2-codex', 'Responses').providerConfig).toEqual({
      runtimeProfileId: OPENAI_RESPONSES_RUNTIME_PROFILE_ID,
    });
  });

  it('builds OpenAI add-model requests with provider-specific runtime profile ids', () => {
    expect(buildAddOpenAiModelRequest('gpt-4.1-mini', 'Completions').providerConfig).toEqual({
      runtimeProfileId: OPENAI_CHAT_RUNTIME_PROFILE_ID,
    });
    expect(buildAddOpenAiModelRequest('gpt-5.2-codex', 'Responses').providerConfig).toEqual({
      runtimeProfileId: OPENAI_RESPONSES_RUNTIME_PROFILE_ID,
    });
  });

  it('builds Gemini add-model requests with model-aware runtime profile ids', () => {
    expect(buildAddGeminiModelRequest('gemini-2.5-flash').providerConfig).toEqual({
      runtimeProfileId: GEMINI_FLASH_RUNTIME_PROFILE_ID,
    });
    expect(buildAddGeminiModelRequest('gemini-2.5-pro').providerConfig).toEqual({
      runtimeProfileId: GEMINI_PRO_RUNTIME_PROFILE_ID,
    });
  });

  it('returns no hf optional warnings when hf providers are active and configured', () => {
    const snapshot = createSnapshot({
      sectionSummaries: [
        ...createSnapshot().sectionSummaries,
        {
          sectionName: HUGGINGFACE_SECTION,
          hasSecrets: true,
          readinessStatus: 'configured',
          missingFields: [],
        },
      ],
      serviceStates: {
        Embeddings: createServiceState('Embeddings', HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings),
        ImageGeneration: createServiceState('ImageGeneration', HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration),
        SpeechTranscription: createServiceState('SpeechTranscription', HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechTranscription),
        SpeechSynthesis: createServiceState('SpeechSynthesis', HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis),
        DocumentIntelligence: createServiceState('DocumentIntelligence', SERVICE_PROVIDER_IDS.DocumentIntelligence),
      },
    });

    expect(summarizeHuggingFaceOptionalServiceWarnings(snapshot)).toEqual([]);
  });

  it('returns no openrouter optional warnings when openrouter providers are active and configured', () => {
    const snapshot = createSnapshot({
      sectionSummaries: [
        ...createSnapshot().sectionSummaries,
        {
          sectionName: OPENROUTER_SECTION,
          hasSecrets: true,
          readinessStatus: 'configured',
          missingFields: [],
        },
      ],
      serviceStates: {
        Embeddings: createServiceState('Embeddings', OPENROUTER_SERVICE_PROVIDER_IDS.Embeddings),
        ImageGeneration: createServiceState('ImageGeneration', OPENROUTER_SERVICE_PROVIDER_IDS.ImageGeneration),
        SpeechTranscription: createServiceState('SpeechTranscription', OPENROUTER_SERVICE_PROVIDER_IDS.SpeechTranscription),
        SpeechSynthesis: createServiceState('SpeechSynthesis', OPENROUTER_SERVICE_PROVIDER_IDS.SpeechSynthesis),
        DocumentIntelligence: createServiceState('DocumentIntelligence', SERVICE_PROVIDER_IDS.DocumentIntelligence),
      },
    });

    expect(summarizeOpenRouterOptionalServiceWarnings(snapshot)).toEqual([]);
  });
});

describe('local ai optional service helpers', () => {
  it('warns when local ai provider cannot activate', () => {
    const snapshot = createSnapshot({
      serviceStates: {
        ...createSnapshot().serviceStates,
        Embeddings: createServiceState('Embeddings', LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings, false),
      },
    });

    expect(summarizeLocalAiOptionalServiceWarnings(snapshot)).toEqual(
      expect.arrayContaining([expect.stringContaining('activation blockers')])
    );
  });

  it('returns no warnings when local ai optional services are ready', () => {
    const snapshot = createSnapshot({
      serviceStates: {
        Embeddings: createServiceState('Embeddings', LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings),
        ImageGeneration: createServiceState('ImageGeneration', LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration),
        SpeechTranscription: createServiceState('SpeechTranscription', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription),
        SpeechSynthesis: createServiceState('SpeechSynthesis', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis),
        DocumentIntelligence: createServiceState(
          'DocumentIntelligence',
          LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence
        ),
      },
    });

    expect(summarizeLocalAiOptionalServiceWarnings(snapshot)).toEqual([]);
  });

  it('warns when local ai service is not selected', () => {
    const snapshot = createSnapshot({
      serviceStates: {
        ...createSnapshot().serviceStates,
        Embeddings: {
          ...createServiceState('Embeddings', LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings),
          activeProviderId: 'other-provider',
        },
      },
    });

    expect(summarizeLocalAiOptionalServiceWarnings(snapshot)[0]).toContain('not set to Local AI');
  });
});

describe('shared wizard form helpers', () => {
  it('returns blank when secret is not stored and value is empty', () => {
    expect(withSecretPreserved('   ', false)).toBe('');
  });

  it('falls back when schema default is missing', () => {
    expect(
      getSchemaDefault({ sections: [{ sectionName: 'OpenAI', properties: [] }] }, 'OpenAI', 'Endpoint', 'fallback')
    ).toBe('fallback');
  });

  it('uses schema default when present', () => {
    expect(
      getSchemaDefault(
        {
          sections: [
            {
              sectionName: 'OpenAI',
              properties: [{ name: 'Endpoint', defaultValue: 'https://api.openai.com/v1' }],
            },
          ],
        },
        'OpenAI',
        'Endpoint',
        'fallback'
      )
    ).toBe('https://api.openai.com/v1');
  });

  it('rejects non-positive integer timeout strings', () => {
    expect(isPositiveIntegerValue('0')).toBe(false);
    expect(isPositiveIntegerValue('12')).toBe(true);
  });
});

describe('local model catalog helpers', () => {
  it('filters and sorts llama-cpp models for existing-model pickers', () => {
    const models = toExistingLocalModels([
      { provider: 'openai-chat', modelId: 'gpt-4o' } as never,
      { provider: 'llama-cpp', modelId: 'qwen-local' } as never,
      { provider: 'llama-cpp', modelId: 'alpha-local' } as never,
    ]);

    expect(models.map((m) => m.modelId)).toEqual(['alpha-local', 'qwen-local']);
  });

  it('throws when local ai draft fails validation', () => {
    expect(() =>
      buildLocalAiModelRequest({
        localId: 'draft-1',
        persisted: false,
        asyncOperationId: null,
        asyncStatus: 'submitted',
        asyncProgress: null,
        asyncError: null,
        setAsGlobalDefault: false,
        installSource: 'huggingface',
        routerModelId: '',
        runtimeProfileId: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: '',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: '',
        catalogDisplayName: '',
      })
    ).toThrow();
  });
});
