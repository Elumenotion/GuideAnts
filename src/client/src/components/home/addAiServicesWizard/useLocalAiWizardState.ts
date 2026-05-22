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
  toExistingLocalModels,
} from './utils';
import {
  createLocalModelOnboardingPoller,
} from '../../../features/localModelOnboarding/useOperationPolling';
import {
  isLocalModelOnboardingInFlight,
  normalizeLocalModelOnboardingStatus,
} from '../../../features/localModelOnboarding/status';

async function persistGlobalDefault(catalogModelId: string): Promise<void> {
  const chatDefaults = await api.settings.chatDefaults.get();
  const request = {
    rowVersion: chatDefaults.rowVersion,
    defaultModelId: catalogModelId,
    overrideAllChatModels: chatDefaults.overrideAllChatModels,
    temperature: chatDefaults.temperature ?? null,
    topP: chatDefaults.topP ?? null,
    reasoningEffort: chatDefaults.reasoningEffort ?? null,
    samplingParametersJson: chatDefaults.samplingParametersJson ?? null,
  };

  try {
    await api.settings.chatDefaults.update(request);
  } catch (error) {
    const body = (error as { body?: unknown })?.body;
    const errors = body && typeof body === 'object'
      ? (body as { errors?: unknown }).errors
      : undefined;
    const hasReasoningEffortError = Array.isArray(errors)
      && errors.some((entry) => typeof entry === 'string' && entry.toLowerCase().includes('reasoningeffort'));

    if (!hasReasoningEffortError) {
      throw error;
    }

    await api.settings.chatDefaults.update({
      ...request,
      reasoningEffort: null,
    });
  }
}

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

export type LocalAiInstallFormData = Omit<LocalAiModelDraft, 'localId' | 'persisted' | 'asyncOperationId' | 'asyncStatus' | 'asyncProgress' | 'asyncError'>;

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
  installError: string | null;
  installModelError: AddModelErrorDto | null;
  modelStepError: string | null;
  readyForBasicChat: boolean;

  setPrereqsForm: (patch: Partial<LocalAiPrerequisitesFormState>) => void;
  setOptionalForm: (patch: Partial<LocalAiOptionalServicesFormState>) => void;
  startInstall: (formData: LocalAiInstallFormData) => Promise<void>;
  removeDraftModel: (localId: string) => void;
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
  const draftModelsRef = useRef<LocalAiModelDraft[]>([]);

  const [installError, setInstallError] = useState<string | null>(null);
  const [installModelError, setInstallModelError] = useState<AddModelErrorDto | null>(null);
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

  const pollingRefs = useRef<Map<string, number>>(new Map());

  useEffect(() => {
    draftModelsRef.current = draftModels;
  }, [draftModels]);

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
    setInstallError(null);
    setInstallModelError(null);
    setModelStepError(null);
  }, []);

  const setPrereqsForm = useCallback((patch: Partial<LocalAiPrerequisitesFormState>) => {
    setPrereqsFormState((previous) => ({ ...previous, ...patch }));
  }, []);

  const setOptionalForm = useCallback((patch: Partial<LocalAiOptionalServicesFormState>) => {
    setOptionalFormState((previous) => ({ ...previous, ...patch }));
  }, []);

  const stopPolling = useCallback((localId: string) => {
    const existing = pollingRefs.current.get(localId);
    if (existing) {
      clearInterval(existing);
      pollingRefs.current.delete(localId);
    }
  }, []);

  const POLL_FAILURE_THRESHOLD = 5;

  const removeDraftModel = useCallback((localId: string) => {
    stopPolling(localId);
    setDraftModels((prev) => prev.filter((d) => d.localId !== localId));
  }, [stopPolling]);

  const pollDownload = useCallback((
    localId: string,
    operationId: string,
    catalogModelId: string,
    shouldSetDefault: boolean
  ) => {
    const interval = createLocalModelOnboardingPoller({
      operationId,
      onUpdate: (op) => {
        setDraftModels((prev) =>
          prev.map((d) => {
            if (d.localId !== localId) return d;
            return {
              ...d,
              asyncStatus: normalizeLocalModelOnboardingStatus(op.status),
              asyncProgress: op.progress ?? null,
              asyncError: op.errorMessage ?? null,
            };
          })
        );
      },
      onTerminal: (op) => {
        stopPolling(localId);
        if (op.status === 'completed' && shouldSetDefault) {
          void (async () => {
            try {
              await persistGlobalDefault(catalogModelId);
            } catch (error) {
              const message = error instanceof Error ? error.message : 'Unknown error.';
              setInstallError(
                `Model was installed, but setting '${catalogModelId}' as global default failed: ${message}`
              );
            }
          })();
        }
      },
      onPollFailureThreshold: () => {
        stopPolling(localId);
        setDraftModels((prev) =>
          prev.map((d) => {
            if (d.localId !== localId) return d;
            return {
              ...d,
              asyncStatus: 'error' as const,
              asyncError: 'Download status is no longer reachable. The runtime container may have restarted.',
            };
          })
        );
      },
      intervalMs: 2000,
      failureThreshold: POLL_FAILURE_THRESHOLD,
    });

    pollingRefs.current.set(localId, interval);
  }, [stopPolling]);

  const startInstall = useCallback(async (formData: LocalAiInstallFormData) => {
    setInstallError(null);
    setInstallModelError(null);

    const localId = `draft-local-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const draft: LocalAiModelDraft = {
      ...formData,
      localId,
      persisted: false,
      asyncOperationId: null,
      asyncStatus: 'submitted',
      asyncProgress: null,
      asyncError: null,
    };

    // Validate before touching state.
    let request;
    try {
      request = buildLocalAiModelRequest(draft);
    } catch (error) {
      setInstallError(error instanceof Error ? error.message : 'Invalid model configuration.');
      return;
    }

    setDraftModels((prev) => [...prev, draft]);

    try {
      const response = await api.settings.addModel(request);
      const operationId = response.operationId ?? null;

      if (operationId) {
        setDraftModels((prev) =>
          prev.map((d) => d.localId === localId ? { ...d, asyncOperationId: operationId, asyncStatus: 'queued' as const } : d)
        );
        pollDownload(localId, operationId, draft.catalogModelId, formData.setAsGlobalDefault);
      } else {
        setDraftModels((prev) =>
          prev.map((d) => d.localId === localId ? { ...d, persisted: true, asyncStatus: 'completed' as const } : d)
        );
        if (formData.setAsGlobalDefault) {
          try {
            await persistGlobalDefault(draft.catalogModelId);
          } catch (error) {
            const message = error instanceof Error ? error.message : 'Unknown error.';
            setInstallError(
              `Model was installed, but setting '${draft.catalogModelId}' as global default failed: ${message}`
            );
          }
        }
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
        setInstallModelError(parsedError);
      } else {
        setInstallError(message);
      }
      setDraftModels((prev) =>
        prev.map((d) => d.localId === localId ? { ...d, asyncStatus: 'error' as const, asyncError: message } : d)
      );
    }
  }, [pollDownload]);

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
    const current = draftModelsRef.current;

    if (existingCount + current.length === 0) {
      setModelStepError('Install at least one local AI model to continue.');
      throw new Error('No models.');
    }

    const hasActiveDownloads = current.some(
      (d) => isLocalModelOnboardingInFlight(d.asyncStatus)
    );
    if (hasActiveDownloads) {
      setModelStepError('Model downloads are in progress — please wait for them to complete before continuing.');
      throw new Error('Downloads in progress.');
    }

    const hasUsableModel = existingCount > 0 || current.some((d) => d.asyncStatus === 'completed');
    if (!hasUsableModel) {
      setModelStepError('No models were installed successfully. Fix any errors and install a model to continue.');
      throw new Error('No usable models.');
    }

    setModelStepError(null);
  }, [draftModels]);

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
    installError,
    installModelError,
    modelStepError,
    readyForBasicChat,
    setPrereqsForm,
    setOptionalForm,
    startInstall,
    removeDraftModel,
    persistLocalAiPrereqs,
    persistLocalAiModels,
    validateLocalAiOptionalServices,
    persistLocalAiOptionalServices,
    resetWithSnapshot,
    loadRuntimeData,
  };
}
