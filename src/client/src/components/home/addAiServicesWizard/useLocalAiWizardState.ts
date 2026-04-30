import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../../../services/api';
import type {
  AddModelErrorDto,
  LlamaRuntimeInventoryItemDto,
  SettingsRuntimeProfileDto,
} from '../../../types/settings';
import {
  HUGGINGFACE_SECTION,
  LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS,
  LOCAL_AI_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
} from './constants';
import type {
  LocalAiModelDraft,
  LocalAiOptionalServicesFormState,
  LocalAiPrerequisitesFormState,
  WizardLoadSnapshot,
} from './types';
import {
  buildLocalAiModelRequest,
  makeLocalAiModelDraft,
  toExistingLocalModels,
} from './utils';

function getServiceProviderFieldValue(
  snapshot: WizardLoadSnapshot,
  serviceKey: keyof WizardLoadSnapshot['serviceStates'],
  providerId: string,
  fieldName: string
): string {
  const state = snapshot.serviceStates[serviceKey];
  const provider = state?.providers.find((p) => p.providerId === providerId);
  const value = provider?.fields?.[fieldName]?.value;
  return typeof value === 'string' ? value : '';
}

function isServiceActive(
  snapshot: WizardLoadSnapshot,
  serviceKey: keyof WizardLoadSnapshot['serviceStates'],
  providerId: string
): boolean {
  const state = snapshot.serviceStates[serviceKey];
  return state?.activeProviderId === providerId;
}

function isPositiveInteger(value: string): boolean {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0;
}

function buildLocalAiPrereqsForm(snapshot: WizardLoadSnapshot): LocalAiPrerequisitesFormState {
  const section = snapshot.sectionsByName[HUGGINGFACE_SECTION];
  return {
    huggingFaceToken: '',
    huggingFaceTokenHasStoredValue: Boolean(section?.secretHasValue?.['Token']),
  };
}

function buildLocalAiOptionalServicesForm(snapshot: WizardLoadSnapshot): LocalAiOptionalServicesFormState {
  const embId = LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings;
  const imgId = LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration;
  const asrId = LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription;
  const ttsId = LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis;
  const docId = LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence;

  return {
    enableEmbeddings: isServiceActive(snapshot, 'Embeddings', embId),
    embeddingsTimeoutSeconds:
      getServiceProviderFieldValue(snapshot, 'Embeddings', embId, 'TimeoutSeconds') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    embeddingsLocalMinIntervalMs:
      getServiceProviderFieldValue(snapshot, 'Embeddings', embId, 'LocalMinIntervalMs') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.embeddingsLocalMinIntervalMs,

    enableImages: isServiceActive(snapshot, 'ImageGeneration', imgId),
    imagesTimeoutSeconds:
      getServiceProviderFieldValue(snapshot, 'ImageGeneration', imgId, 'TimeoutSeconds') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    imagesLocalOutputFormat:
      getServiceProviderFieldValue(snapshot, 'ImageGeneration', imgId, 'LocalOutputFormat') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.imagesLocalOutputFormat,

    enableSpeechTranscription: isServiceActive(snapshot, 'SpeechTranscription', asrId),
    speechTranscriptionTimeoutSeconds:
      getServiceProviderFieldValue(snapshot, 'SpeechTranscription', asrId, 'TimeoutSeconds') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,

    enableSpeechSynthesis: isServiceActive(snapshot, 'SpeechSynthesis', ttsId),
    speechSynthesisTimeoutSeconds:
      getServiceProviderFieldValue(snapshot, 'SpeechSynthesis', ttsId, 'TimeoutSeconds') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,

    enableDocumentIntelligence: isServiceActive(snapshot, 'DocumentIntelligence', docId),
    documentIntelligenceTimeoutSeconds:
      getServiceProviderFieldValue(snapshot, 'DocumentIntelligence', docId, 'TimeoutSeconds') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.documentIntelligenceTimeoutSeconds,
    documentIntelligenceMaxConcurrentConversions:
      getServiceProviderFieldValue(snapshot, 'DocumentIntelligence', docId, 'MaxConcurrentConversions') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.documentIntelligenceMaxConcurrentConversions,
    documentIntelligenceAsyncStatusPollIntervalMs:
      getServiceProviderFieldValue(snapshot, 'DocumentIntelligence', docId, 'AsyncStatusPollIntervalMs') ||
      LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.documentIntelligenceAsyncStatusPollIntervalMs,
  };
}

export interface UseLocalAiWizardStateResult {
  prereqsForm: LocalAiPrerequisitesFormState;
  prereqsErrors: Partial<Record<'huggingFaceToken', string>>;
  draftModels: LocalAiModelDraft[];
  existingLocalModels: ReturnType<typeof toExistingLocalModels>;
  optionalForm: LocalAiOptionalServicesFormState;
  optionalErrors: Record<string, string>;
  profiles: SettingsRuntimeProfileDto[];
  profilesLoading: boolean;
  inventory: LlamaRuntimeInventoryItemDto[];
  inventoryLoading: boolean;
  addError: string | null;
  addModelError: AddModelErrorDto | null;
  modelStepError: string | null;
  readyForBasicChat: boolean;

  setPrereqsForm: (patch: Partial<LocalAiPrerequisitesFormState>) => void;
  setOptionalForm: (patch: Partial<LocalAiOptionalServicesFormState>) => void;
  addDraftModel: (draftData: Omit<LocalAiModelDraft, 'localId' | 'persisted' | 'asyncOperationId' | 'asyncStatus' | 'asyncProgress' | 'asyncError'>) => void;
  removeDraftModel: (localId: string) => void;
  installDraftModel: (localId: string) => Promise<void>;
  persistLocalAiPrereqs: (snapshot: WizardLoadSnapshot, loadSnapshot: () => Promise<WizardLoadSnapshot>, setSnapshot: (s: WizardLoadSnapshot) => void) => Promise<void>;
  persistLocalAiModels: (snapshot: WizardLoadSnapshot, loadSnapshot: () => Promise<WizardLoadSnapshot>, setSnapshot: (s: WizardLoadSnapshot) => void) => Promise<void>;
  validateLocalAiOptionalServices: () => boolean;
  persistLocalAiOptionalServices: (loadSnapshot: () => Promise<WizardLoadSnapshot>, setSnapshot: (s: WizardLoadSnapshot) => void) => Promise<void>;
  resetWithSnapshot: (snapshot: WizardLoadSnapshot) => void;
  loadRuntimeData: () => void;
}

export function useLocalAiWizardState(): UseLocalAiWizardStateResult {
  const [prereqsForm, setPrereqsFormState] = useState<LocalAiPrerequisitesFormState>({
    huggingFaceToken: '',
    huggingFaceTokenHasStoredValue: false,
  });
  const [prereqsErrors, setPrereqsErrors] = useState<Partial<Record<'huggingFaceToken', string>>>({});

  const [draftModels, setDraftModels] = useState<LocalAiModelDraft[]>([]);
  const [addError, setAddError] = useState<string | null>(null);
  const [addModelError, setAddModelError] = useState<AddModelErrorDto | null>(null);
  const [modelStepError, setModelStepError] = useState<string | null>(null);

  const [optionalForm, setOptionalFormState] = useState<LocalAiOptionalServicesFormState>({
    enableEmbeddings: false,
    embeddingsTimeoutSeconds: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.embeddingsTimeoutSeconds,
    embeddingsLocalMinIntervalMs: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.embeddingsLocalMinIntervalMs,
    enableImages: false,
    imagesTimeoutSeconds: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.imagesTimeoutSeconds,
    imagesLocalOutputFormat: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.imagesLocalOutputFormat,
    enableSpeechTranscription: false,
    speechTranscriptionTimeoutSeconds: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.speechTranscriptionTimeoutSeconds,
    enableSpeechSynthesis: false,
    speechSynthesisTimeoutSeconds: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.speechSynthesisTimeoutSeconds,
    enableDocumentIntelligence: false,
    documentIntelligenceTimeoutSeconds: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.documentIntelligenceTimeoutSeconds,
    documentIntelligenceMaxConcurrentConversions: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.documentIntelligenceMaxConcurrentConversions,
    documentIntelligenceAsyncStatusPollIntervalMs: LOCAL_AI_OPTIONAL_SERVICE_DEFAULTS.documentIntelligenceAsyncStatusPollIntervalMs,
  });
  const [optionalErrors, setOptionalErrors] = useState<Record<string, string>>({});

  const [profiles, setProfiles] = useState<SettingsRuntimeProfileDto[]>([]);
  const [profilesLoading, setProfilesLoading] = useState(false);
  const [inventory, setInventory] = useState<LlamaRuntimeInventoryItemDto[]>([]);
  const [inventoryLoading, setInventoryLoading] = useState(false);

  const [snapshot, setLocalSnapshot] = useState<WizardLoadSnapshot | null>(null);

  const pollingRefs = useRef<Map<string, ReturnType<typeof setInterval>>>(new Map());

  useEffect(() => {
    return () => {
      for (const interval of pollingRefs.current.values()) {
        clearInterval(interval);
      }
    };
  }, []);

  const existingLocalModels = snapshot ? toExistingLocalModels(snapshot.models) : [];
  const readyForBasicChat = existingLocalModels.length > 0 || draftModels.some((d) => d.asyncStatus === 'completed');

  const loadRuntimeData = useCallback(() => {
    setProfilesLoading(true);
    void api.settings.getRuntimeProfiles().then((data) => {
      setProfiles(data);
      setProfilesLoading(false);
    }).catch(() => {
      setProfilesLoading(false);
    });

    setInventoryLoading(true);
    void api.settings.getLlamaInventory().then((data) => {
      setInventory(data);
      setInventoryLoading(false);
    }).catch(() => {
      setInventoryLoading(false);
    });
  }, []);

  const resetWithSnapshot = useCallback((nextSnapshot: WizardLoadSnapshot) => {
    setLocalSnapshot(nextSnapshot);
    setPrereqsFormState(buildLocalAiPrereqsForm(nextSnapshot));
    setOptionalFormState(buildLocalAiOptionalServicesForm(nextSnapshot));
    setDraftModels([]);
    setPrereqsErrors({});
    setOptionalErrors({});
    setAddError(null);
    setAddModelError(null);
    setModelStepError(null);
  }, []);

  const setPrereqsForm = useCallback((patch: Partial<LocalAiPrerequisitesFormState>) => {
    setPrereqsFormState((previous) => ({ ...previous, ...patch }));
  }, []);

  const setOptionalForm = useCallback((patch: Partial<LocalAiOptionalServicesFormState>) => {
    setOptionalFormState((previous) => ({ ...previous, ...patch }));
  }, []);

  const addDraftModel = useCallback((
    draftData: Omit<LocalAiModelDraft, 'localId' | 'persisted' | 'asyncOperationId' | 'asyncStatus' | 'asyncProgress' | 'asyncError'>
  ) => {
    const localId = `draft-local-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const draft = makeLocalAiModelDraft(localId, draftData.setAsGlobalDefault);
    const filled: LocalAiModelDraft = { ...draft, ...draftData, localId };
    setDraftModels((prev) => [...prev, filled]);
    setAddError(null);
    setAddModelError(null);
  }, []);

  const removeDraftModel = useCallback((localId: string) => {
    const existing = pollingRefs.current.get(localId);
    if (existing) {
      clearInterval(existing);
      pollingRefs.current.delete(localId);
    }
    setDraftModels((prev) => prev.filter((d) => d.localId !== localId));
  }, []);

  const pollDownload = useCallback((localId: string, operationId: string) => {
    const interval = setInterval(() => {
      void (async () => {
        try {
          const op = await api.settings.getDownloadStatus(operationId);
          setDraftModels((prev) =>
            prev.map((d) => {
              if (d.localId !== localId) return d;
              const done = op.status === 'completed' || op.status === 'failed' || op.status === 'error';
              if (done) {
                const existing = pollingRefs.current.get(localId);
                if (existing) {
                  clearInterval(existing);
                  pollingRefs.current.delete(localId);
                }
              }
              return {
                ...d,
                asyncStatus: op.status === 'completed' ? 'completed' : op.status === 'failed' || op.status === 'error' ? 'error' : 'downloading',
                asyncProgress: op.progress ?? null,
                asyncError: op.errorMessage ?? null,
              };
            })
          );
        } catch {
          // silently ignore poll errors
        }
      })();
    }, 2000);
    pollingRefs.current.set(localId, interval);
  }, []);

  const installDraftModel = useCallback(async (localId: string) => {
    const draft = draftModels.find((d) => d.localId === localId);
    if (!draft || draft.asyncStatus !== 'pending') return;

    setDraftModels((prev) =>
      prev.map((d) => d.localId === localId ? { ...d, asyncStatus: 'submitted' as const } : d)
    );
    setAddModelError(null);

    try {
      const request = buildLocalAiModelRequest(draft);
      const response = await api.settings.addModel(request);

      const operationId = response.operationId ?? null;

      if (operationId) {
        setDraftModels((prev) =>
          prev.map((d) => d.localId === localId ? { ...d, asyncOperationId: operationId, asyncStatus: 'downloading' as const } : d)
        );
        pollDownload(localId, operationId);
      } else {
        setDraftModels((prev) =>
          prev.map((d) => d.localId === localId ? { ...d, persisted: true, asyncStatus: 'completed' as const } : d)
        );
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to install model.';
      let parsedError: AddModelErrorDto | null = null;
      try {
        if (error instanceof Error && (error as { body?: unknown }).body) {
          const body = (error as { body?: unknown }).body;
          if (typeof body === 'object' && body !== null) {
            const candidate = body as Partial<AddModelErrorDto>;
            if (typeof candidate.code === 'string' && typeof candidate.message === 'string') {
              parsedError = candidate as AddModelErrorDto;
            }
          }
        }
      } catch {
        // ignore parse errors
      }

      if (parsedError) {
        setAddModelError(parsedError);
      } else {
        setAddError(message);
      }
      setDraftModels((prev) =>
        prev.map((d) => d.localId === localId ? { ...d, asyncStatus: 'error' as const, asyncError: message } : d)
      );
    }
  }, [draftModels, pollDownload]);

  const persistLocalAiPrereqs = useCallback(async (
    snap: WizardLoadSnapshot,
    loadSnapshotFn: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ) => {
    const hfSection = snap.sectionsByName[HUGGINGFACE_SECTION];
    if (!hfSection) {
      return;
    }

    const tokenValue = prereqsForm.huggingFaceToken.trim();
    if (!tokenValue && !prereqsForm.huggingFaceTokenHasStoredValue) {
      return;
    }

    const effectiveToken = tokenValue || SECRET_MASK;
    await api.settings.updateSection(HUGGINGFACE_SECTION, {
      rowVersion: hfSection.rowVersion,
      payload: { Token: effectiveToken },
    });

    const refreshed = await loadSnapshotFn();
    setSnapshot(refreshed);
    setLocalSnapshot(refreshed);

    if (tokenValue) {
      setPrereqsFormState((previous) => ({
        ...previous,
        huggingFaceToken: '',
        huggingFaceTokenHasStoredValue: true,
      }));
    }
  }, [prereqsForm.huggingFaceToken, prereqsForm.huggingFaceTokenHasStoredValue]);

  const persistLocalAiModels = useCallback(async (
    snap: WizardLoadSnapshot,
    _loadSnapshotFn: () => Promise<WizardLoadSnapshot>,
    _setSnapshot: (s: WizardLoadSnapshot) => void
  ) => {
    const existingCount = toExistingLocalModels(snap.models).length;
    const pendingDrafts = draftModels.filter((d) => d.asyncStatus === 'pending');

    if (existingCount + draftModels.length === 0 && pendingDrafts.length === 0) {
      setModelStepError('At least one local AI model must be queued for installation.');
      throw new Error('Model requirement not met.');
    }

    for (const draft of pendingDrafts) {
      await installDraftModel(draft.localId);
    }
    setModelStepError(null);
  }, [draftModels, installDraftModel]);

  const validateLocalAiOptionalServices = useCallback((): boolean => {
    const errors: Record<string, string> = {};

    if (optionalForm.enableEmbeddings) {
      if (!isPositiveInteger(optionalForm.embeddingsTimeoutSeconds)) {
        errors.embeddingsTimeoutSeconds = 'Timeout must be a positive integer.';
      }
      const minInterval = Number(optionalForm.embeddingsLocalMinIntervalMs);
      if (!Number.isInteger(minInterval) || minInterval < 0) {
        errors.embeddingsLocalMinIntervalMs = 'Min interval must be a non-negative integer.';
      }
    }

    if (optionalForm.enableImages) {
      if (!isPositiveInteger(optionalForm.imagesTimeoutSeconds)) {
        errors.imagesTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableSpeechTranscription) {
      if (!isPositiveInteger(optionalForm.speechTranscriptionTimeoutSeconds)) {
        errors.speechTranscriptionTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableSpeechSynthesis) {
      if (!isPositiveInteger(optionalForm.speechSynthesisTimeoutSeconds)) {
        errors.speechSynthesisTimeoutSeconds = 'Timeout must be a positive integer.';
      }
    }

    if (optionalForm.enableDocumentIntelligence) {
      if (!isPositiveInteger(optionalForm.documentIntelligenceTimeoutSeconds)) {
        errors.documentIntelligenceTimeoutSeconds = 'Timeout must be a positive integer.';
      }
      if (!isPositiveInteger(optionalForm.documentIntelligenceMaxConcurrentConversions)) {
        errors.documentIntelligenceMaxConcurrentConversions = 'Max concurrent conversions must be a positive integer.';
      }
      if (!isPositiveInteger(optionalForm.documentIntelligenceAsyncStatusPollIntervalMs)) {
        errors.documentIntelligenceAsyncStatusPollIntervalMs = 'Poll interval must be a positive integer.';
      }
    }

    setOptionalErrors(errors);
    return Object.keys(errors).length === 0;
  }, [optionalForm]);

  const persistLocalAiOptionalServices = useCallback(async (
    loadSnapshotFn: () => Promise<WizardLoadSnapshot>,
    setSnapshot: (s: WizardLoadSnapshot) => void
  ) => {
    if (!validateLocalAiOptionalServices()) {
      throw new Error('Optional service inputs are incomplete.');
    }

    if (optionalForm.enableEmbeddings) {
      await api.settings.services.updateProviderFields('Embeddings', LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings, {
        TimeoutSeconds: optionalForm.embeddingsTimeoutSeconds.trim(),
        LocalMinIntervalMs: optionalForm.embeddingsLocalMinIntervalMs.trim(),
      });
      await api.settings.services.updateActiveProvider('Embeddings', LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings);
    }

    if (optionalForm.enableImages) {
      const imgFields: Record<string, string> = {
        TimeoutSeconds: optionalForm.imagesTimeoutSeconds.trim(),
      };
      const outputFormat = optionalForm.imagesLocalOutputFormat.trim();
      if (outputFormat) {
        imgFields.LocalOutputFormat = outputFormat;
      }
      await api.settings.services.updateProviderFields('ImageGeneration', LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration, imgFields);
      await api.settings.services.updateActiveProvider('ImageGeneration', LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration);
    }

    if (optionalForm.enableSpeechTranscription) {
      await api.settings.services.updateProviderFields('SpeechTranscription', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription, {
        TimeoutSeconds: optionalForm.speechTranscriptionTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechTranscription', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription);
    }

    if (optionalForm.enableSpeechSynthesis) {
      await api.settings.services.updateProviderFields('SpeechSynthesis', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis, {
        TimeoutSeconds: optionalForm.speechSynthesisTimeoutSeconds.trim(),
      });
      await api.settings.services.updateActiveProvider('SpeechSynthesis', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis);
    }

    if (optionalForm.enableDocumentIntelligence) {
      await api.settings.services.updateProviderFields('DocumentIntelligence', LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence, {
        TimeoutSeconds: optionalForm.documentIntelligenceTimeoutSeconds.trim(),
        MaxConcurrentConversions: optionalForm.documentIntelligenceMaxConcurrentConversions.trim(),
        AsyncStatusPollIntervalMs: optionalForm.documentIntelligenceAsyncStatusPollIntervalMs.trim(),
      });
      await api.settings.services.updateActiveProvider('DocumentIntelligence', LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence);
    }

    const refreshed = await loadSnapshotFn();
    setSnapshot(refreshed);
    setLocalSnapshot(refreshed);
    setOptionalErrors({});
  }, [optionalForm, validateLocalAiOptionalServices]);

  return {
    prereqsForm,
    prereqsErrors,
    draftModels,
    existingLocalModels,
    optionalForm,
    optionalErrors,
    profiles,
    profilesLoading,
    inventory,
    inventoryLoading,
    addError,
    addModelError,
    modelStepError,
    readyForBasicChat,
    setPrereqsForm,
    setOptionalForm,
    addDraftModel,
    removeDraftModel,
    installDraftModel,
    persistLocalAiPrereqs,
    persistLocalAiModels,
    validateLocalAiOptionalServices,
    persistLocalAiOptionalServices,
    resetWithSnapshot,
    loadRuntimeData,
  };
}
