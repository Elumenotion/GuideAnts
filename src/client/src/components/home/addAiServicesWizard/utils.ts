import type { AddModelRequest, ProviderEditorStateDto, ServiceEditorStateDto, SettingsModelDto, SettingsSectionSummaryDto } from '../../../types/settings';
import {
  DOCUMENT_INTELLIGENCE_SECTION,
  EMBEDDINGS_SECTION,
  IMAGES_SECTION,
  MODEL_PROVIDER_ID_TO_LABEL,
  MODEL_PROVIDER_LABEL_TO_ID,
  SERVICE_PROVIDER_IDS,
  SPEECH_SECTION,
} from './constants';
import type {
  ExistingFoundryModel,
  FoundryModelDraft,
  FoundryModelProviderLabel,
  OptionalServiceKey,
  WizardLoadSnapshot,
} from './types';

export function mapProviderLabelToModelProviderId(value: FoundryModelProviderLabel): string {
  return MODEL_PROVIDER_LABEL_TO_ID[value];
}

export function mapModelProviderIdToLabel(value: string): FoundryModelProviderLabel | null {
  return MODEL_PROVIDER_ID_TO_LABEL[value] ?? null;
}

export function deriveEndpointFromResource(resource: string): string {
  const trimmed = resource.trim();
  if (!trimmed) {
    return '';
  }
  return `https://${trimmed}.openai.azure.com/`;
}

export function toExistingFoundryModels(models: SettingsModelDto[]): ExistingFoundryModel[] {
  return models
    .map((model) => {
      const providerLabel = mapModelProviderIdToLabel(model.provider);
      if (!providerLabel) {
        return null;
      }
      return {
        modelId: model.modelId,
        provider: providerLabel,
        raw: model,
      } satisfies ExistingFoundryModel;
    })
    .filter((item): item is ExistingFoundryModel => item !== null)
    .sort((left, right) => left.modelId.localeCompare(right.modelId));
}

export function makeDraftModel(localId: string, modelId: string, provider: FoundryModelProviderLabel): FoundryModelDraft {
  return {
    localId,
    modelId: modelId.trim(),
    provider,
    persisted: false,
  };
}

export function hasModelTuple(
  models: readonly Pick<FoundryModelDraft, 'modelId' | 'provider'>[],
  candidate: Pick<FoundryModelDraft, 'modelId' | 'provider'>
): boolean {
  return models.some(
    (model) =>
      model.modelId.trim().toLowerCase() === candidate.modelId.trim().toLowerCase()
      && model.provider === candidate.provider
  );
}

export function hasModelId(models: readonly Pick<FoundryModelDraft, 'modelId'>[], modelId: string): boolean {
  const normalized = modelId.trim().toLowerCase();
  return models.some((model) => model.modelId.trim().toLowerCase() === normalized);
}

export function buildAddModelRequest(modelId: string, provider: FoundryModelProviderLabel): AddModelRequest {
  const trimmed = modelId.trim();
  return {
    provider: mapProviderLabelToModelProviderId(provider),
    catalog: {
      modelId: trimmed,
      displayName: trimmed,
      isActive: true,
    },
  };
}

function findSectionSummary(
  sectionSummaries: readonly SettingsSectionSummaryDto[],
  sectionName: string
): SettingsSectionSummaryDto | null {
  return sectionSummaries.find((section) => section.sectionName === sectionName) ?? null;
}

function getProviderState(state: ServiceEditorStateDto | undefined, providerId: string): ProviderEditorStateDto | null {
  if (!state) {
    return null;
  }
  return state.providers.find((provider) => provider.providerId === providerId) ?? null;
}

function optionalServiceStatus(
  serviceKey: OptionalServiceKey,
  snapshot: WizardLoadSnapshot
): { complete: boolean; message: string } {
  if (serviceKey === 'Embeddings') {
    const section = findSectionSummary(snapshot.sectionSummaries, EMBEDDINGS_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Embeddings connection is not configured.' };
    }

    const state = snapshot.serviceStates.Embeddings;
    const providerId = SERVICE_PROVIDER_IDS.Embeddings;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Embeddings service is not set to Microsot Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Embeddings service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Embeddings is ready.' };
  }

  if (serviceKey === 'ImageGeneration') {
    const section = findSectionSummary(snapshot.sectionSummaries, IMAGES_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Image Generation connection is not configured.' };
    }

    const state = snapshot.serviceStates.ImageGeneration;
    const providerId = SERVICE_PROVIDER_IDS.ImageGeneration;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Image Generation service is not set to Microsot Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Image Generation service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Image Generation is ready.' };
  }

  if (serviceKey === 'SpeechTranscription') {
    const section = findSectionSummary(snapshot.sectionSummaries, SPEECH_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Speech connection is not configured for transcription.' };
    }

    const state = snapshot.serviceStates.SpeechTranscription;
    const providerId = SERVICE_PROVIDER_IDS.SpeechTranscription;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Transcription service is not set to Microsot Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Transcription service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Transcription is ready.' };
  }

  if (serviceKey === 'SpeechSynthesis') {
    const section = findSectionSummary(snapshot.sectionSummaries, SPEECH_SECTION);
    if (!section || section.readinessStatus !== 'configured') {
      return { complete: false, message: 'Speech connection is not configured for synthesis.' };
    }

    const state = snapshot.serviceStates.SpeechSynthesis;
    const providerId = SERVICE_PROVIDER_IDS.SpeechSynthesis;
    const provider = getProviderState(state, providerId);
    if (!state || !provider || state.activeProviderId !== providerId) {
      return { complete: false, message: 'Speech Synthesis service is not set to Microsot Foundry.' };
    }
    if (!provider.canActivate) {
      return { complete: false, message: 'Speech Synthesis service has unresolved activation blockers.' };
    }
    return { complete: true, message: 'Speech Synthesis is ready.' };
  }

  const section = findSectionSummary(snapshot.sectionSummaries, DOCUMENT_INTELLIGENCE_SECTION);
  if (!section || section.readinessStatus !== 'configured') {
    return { complete: false, message: 'Document Intelligence connection is not configured.' };
  }

  const state = snapshot.serviceStates.DocumentIntelligence;
  const providerId = SERVICE_PROVIDER_IDS.DocumentIntelligence;
  const provider = getProviderState(state, providerId);
  if (!state || !provider || state.activeProviderId !== providerId) {
    return { complete: false, message: 'Document Intelligence service is not set to Microsot Foundry.' };
  }
  if (!provider.canActivate) {
    return { complete: false, message: 'Document Intelligence service has unresolved activation blockers.' };
  }
  return { complete: true, message: 'Document Intelligence is ready.' };
}

export function summarizeOptionalServiceWarnings(snapshot: WizardLoadSnapshot): string[] {
  const keys: OptionalServiceKey[] = [
    'Embeddings',
    'ImageGeneration',
    'SpeechTranscription',
    'SpeechSynthesis',
    'DocumentIntelligence',
  ];
  const warnings: string[] = [];
  for (const key of keys) {
    const result = optionalServiceStatus(key, snapshot);
    if (!result.complete) {
      warnings.push(result.message);
    }
  }
  return warnings;
}

