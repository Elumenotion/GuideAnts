import { describe, expect, it } from 'vitest';
import type { ProviderEditorStateDto, ServiceEditorStateDto } from '../../../../types/settings';
import {
  DOCUMENT_INTELLIGENCE_SECTION,
  EMBEDDINGS_SECTION,
  GEMINI_CORE_SECTION,
  GEMINI_SERVICE_PROVIDER_IDS,
  IMAGES_SECTION,
  SERVICE_PROVIDER_IDS,
  SPEECH_SECTION,
} from '../constants';
import {
  deriveEndpointFromResource,
  hasModelId,
  hasModelTuple,
  mapModelProviderIdToLabel,
  mapProviderLabelToModelProviderId,
  summarizeGeminiOptionalServiceWarnings,
  summarizeOptionalServiceWarnings,
  toExistingGeminiModels,
  toExistingFoundryModels,
} from '../utils';
import type { OptionalServiceKey, WizardLoadSnapshot } from '../types';

function createProvider(providerId: string, canActivate = true): ProviderEditorStateDto {
  return {
    providerId,
    providerKind: 'Cloud',
    displayName: providerId,
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
    displayName: serviceId,
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
        displayName: 'Embeddings',
        displayOrder: 1,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
      {
        sectionName: IMAGES_SECTION,
        displayName: 'Images',
        displayOrder: 2,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
      {
        sectionName: SPEECH_SECTION,
        displayName: 'Speech',
        displayOrder: 3,
        hasSecrets: true,
        readinessStatus: 'configured',
        missingFields: [],
      },
      {
        sectionName: DOCUMENT_INTELLIGENCE_SECTION,
        displayName: 'Document Intelligence',
        displayOrder: 4,
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
          displayName: 'Google Gemini API',
          displayOrder: 9,
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
});
