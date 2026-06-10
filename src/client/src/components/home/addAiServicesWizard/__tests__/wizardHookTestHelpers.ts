import { vi } from 'vitest';
import type { ProviderEditorStateDto, ServiceEditorStateDto, SettingsSectionDto } from '../../../../types/settings';
import type { OptionalServiceKey, WizardLoadSnapshot } from '../types';
import {
  DOCUMENT_INTELLIGENCE_SECTION,
  EMBEDDINGS_SECTION,
  HUGGINGFACE_SECTION,
  IMAGES_SECTION,
  OPENAI_CORE_SECTION,
  OPENROUTER_SECTION,
  SERVICE_PROVIDER_IDS,
  SPEECH_SECTION,
} from '../constants';

export function createProvider(providerId: string, canActivate = true): ProviderEditorStateDto {
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

export function createServiceState(serviceId: OptionalServiceKey, providerId: string, canActivate = true): ServiceEditorStateDto {
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

export function createSection(sectionName: string, payload: Record<string, unknown> = {}, secretHasValue: Record<string, boolean> = {}): SettingsSectionDto {
  return {
    sectionName,
    schemaVersion: 1,
    rowVersion: '1',
    updatedUtc: '2026-04-29T00:00:00Z',
    payload,
    secretHasValue,
  };
}

export function createWizardSnapshot(overrides?: Partial<WizardLoadSnapshot>): WizardLoadSnapshot {
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
    sectionsByName: {
      [OPENAI_CORE_SECTION]: createSection(OPENAI_CORE_SECTION),
      [HUGGINGFACE_SECTION]: createSection(HUGGINGFACE_SECTION, { RouterBaseUrl: 'https://router.huggingface.co/v1' }),
      [OPENROUTER_SECTION]: createSection(OPENROUTER_SECTION, { BaseUrl: 'https://openrouter.ai/api/v1' }),
    },
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

export function createLoadSnapshot(snapshot: WizardLoadSnapshot) {
  return vi.fn(async () => ({ ...snapshot }));
}

export function createSetSnapshot() {
  return vi.fn();
}
