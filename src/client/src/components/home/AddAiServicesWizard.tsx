import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../../services/api';
import type { ServiceEditorStateDto, SettingsSectionDto } from '../../types/settings';
import { SettingsModal } from '../../pages/settings/components/shared/SettingsModal';
import {
  FOUNDRY_CORE_SECTION,
  FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
  FOUNDRY_EMBEDDINGS_SECTION,
  FOUNDRY_IMAGES_SECTION,
  FOUNDRY_SPEECH_SECTION,
  GEMINI_CORE_SECTION,
  HUGGINGFACE_SECTION,
  LOCAL_AI_WIZARD_STEPS,
  OPENAI_CORE_SECTION,
  WIZARD_STEPS,
} from './addAiServicesWizard/constants';
import { CoreConnectionStep } from './addAiServicesWizard/steps/CoreConnectionStep';
import { FinishStep } from './addAiServicesWizard/steps/FinishStep';
import { GeminiConnectionStep } from './addAiServicesWizard/steps/GeminiConnectionStep';
import { HuggingFaceConnectionStep } from './addAiServicesWizard/steps/HuggingFaceConnectionStep';
import { HuggingFaceModelsStep } from './addAiServicesWizard/steps/HuggingFaceModelsStep';
import { HuggingFaceOptionalServicesStep } from './addAiServicesWizard/steps/HuggingFaceOptionalServicesStep';
import { GeminiModelsStep } from './addAiServicesWizard/steps/GeminiModelsStep';
import { GeminiOptionalServicesStep } from './addAiServicesWizard/steps/GeminiOptionalServicesStep';
import { DraftProgress, LocalAiModelsStep } from './addAiServicesWizard/steps/LocalAiModelsStep';
import { LocalAiPrerequisitesStep } from './addAiServicesWizard/steps/LocalAiPrerequisitesStep';
import { LocalAiSpeechTranscriptionStep } from './addAiServicesWizard/steps/LocalAiSpeechTranscriptionStep';
import { LocalAiImageGenerationStep } from './addAiServicesWizard/steps/LocalAiImageGenerationStep';
import { LocalAiSpeechSynthesisStep } from './addAiServicesWizard/steps/LocalAiSpeechSynthesisStep';
import { LocalAiDocumentIntelligenceStep } from './addAiServicesWizard/steps/LocalAiDocumentIntelligenceStep';
import { LocalAiEmbeddingsStep } from './addAiServicesWizard/steps/LocalAiEmbeddingsStep';
import type { LocalAiServiceStepHandle } from './addAiServicesWizard/steps/LocalAiServiceStepBase';
import { ModelsStep } from './addAiServicesWizard/steps/ModelsStep';
import { OpenAiConnectionStep } from './addAiServicesWizard/steps/OpenAiConnectionStep';
import { OpenAiModelsStep } from './addAiServicesWizard/steps/OpenAiModelsStep';
import { OpenAiOptionalServicesStep } from './addAiServicesWizard/steps/OpenAiOptionalServicesStep';
import { OptionalServicesStep } from './addAiServicesWizard/steps/OptionalServicesStep';
import { ProviderStep } from './addAiServicesWizard/steps/ProviderStep';
import type { LocalDownloadOperationState } from '../../pages/settings/editors/common/localOperationPolling';
import type {
  AddAiServicesWizardProvider,
  AddAiServicesWizardStep,
  OptionalServiceKey,
  WizardLoadSnapshot,
} from './addAiServicesWizard/types';
import {
  getSchemaDefault,
  summarizeFoundryOptionalServiceWarnings,
  summarizeGeminiOptionalServiceWarnings,
  summarizeHuggingFaceOptionalServiceWarnings,
  summarizeLocalAiOptionalServiceWarnings,
  summarizeOpenAiOptionalServiceWarnings,
  toExistingFoundryModels,
  toExistingGeminiModels,
  toExistingHuggingFaceModels,
  toExistingLocalModels,
  toExistingOpenAiModels,
} from './addAiServicesWizard/utils';
import { useFoundryWizardState } from './addAiServicesWizard/useFoundryWizardState';
import { useGeminiWizardState } from './addAiServicesWizard/useGeminiWizardState';
import { useHuggingFaceWizardState } from './addAiServicesWizard/useHuggingFaceWizardState';
import { useLocalAiWizardState } from './addAiServicesWizard/useLocalAiWizardState';
import { useOpenAiWizardState } from './addAiServicesWizard/useOpenAiWizardState';

interface AddAiServicesWizardProps {
  isOpen: boolean;
  onDismiss: (persistDismissal: boolean) => void;
  onOpenSettings: (persistDismissal: boolean) => void;
}

const STEP_SEQUENCE_BY_PROVIDER: Readonly<Record<AddAiServicesWizardProvider, readonly AddAiServicesWizardStep[]>> = {
  foundry: ['provider', 'connection', 'models', 'optionalServices', 'finish'],
  'google-gemini': ['provider', 'connection', 'models', 'optionalServices', 'finish'],
  openai: ['provider', 'connection', 'models', 'optionalServices', 'finish'],
  huggingface: ['provider', 'connection', 'models', 'optionalServices', 'finish'],
  'local-ai': [
    'provider',
    'connection',
    'models',
    'localAiSpeechTranscription',
    'localAiImageGeneration',
    'localAiSpeechSynthesis',
    'localAiDocumentIntelligence',
    'localAiEmbeddings',
    'finish',
  ],
} as const;

const STEP_TABS_BY_PROVIDER: Readonly<Record<AddAiServicesWizardProvider, readonly { id: string; label: string }[]>> = {
  foundry: WIZARD_STEPS,
  'google-gemini': WIZARD_STEPS,
  openai: WIZARD_STEPS,
  huggingface: WIZARD_STEPS,
  'local-ai': LOCAL_AI_WIZARD_STEPS,
} as const;

function getStepSequence(provider: AddAiServicesWizardProvider): AddAiServicesWizardStep[] {
  return [...STEP_SEQUENCE_BY_PROVIDER[provider]];
}

function nextStep(provider: AddAiServicesWizardProvider, current: AddAiServicesWizardStep): AddAiServicesWizardStep {
  const sequence = getStepSequence(provider);
  const currentIndex = sequence.indexOf(current);
  if (currentIndex === -1) {
    return sequence[0] ?? 'provider';
  }
  return sequence[Math.min(currentIndex + 1, sequence.length - 1)] ?? 'finish';
}

function previousStep(provider: AddAiServicesWizardProvider, current: AddAiServicesWizardStep): AddAiServicesWizardStep {
  const sequence = getStepSequence(provider);
  const currentIndex = sequence.indexOf(current);
  if (currentIndex === -1) {
    return sequence[0] ?? 'provider';
  }
  return sequence[Math.max(currentIndex - 1, 0)] ?? 'provider';
}

function isLocalAiServiceStep(step: AddAiServicesWizardStep): step is
  | 'localAiSpeechTranscription'
  | 'localAiImageGeneration'
  | 'localAiSpeechSynthesis'
  | 'localAiDocumentIntelligence'
  | 'localAiEmbeddings' {
  return step === 'localAiSpeechTranscription'
    || step === 'localAiImageGeneration'
    || step === 'localAiSpeechSynthesis'
    || step === 'localAiDocumentIntelligence'
    || step === 'localAiEmbeddings';
}

const OPTIONAL_SERVICE_KEYS: OptionalServiceKey[] = [
  'Embeddings',
  'ImageGeneration',
  'SpeechTranscription',
  'SpeechSynthesis',
  'DocumentIntelligence',
];

export default function AddAiServicesWizard({ isOpen, onDismiss, onOpenSettings }: AddAiServicesWizardProps) {
  const [step, setStep] = useState<AddAiServicesWizardStep>('provider');
  const [provider, setProvider] = useState<AddAiServicesWizardProvider>('foundry');
  const [dontAutoOpenAgain, setDontAutoOpenAgain] = useState(false);

  const [snapshot, setSnapshot] = useState<WizardLoadSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [globalError, setGlobalError] = useState<string | null>(null);
  const [localAiStepOperation, setLocalAiStepOperation] = useState<LocalDownloadOperationState | null>(null);
  const [cancellingLocalAiOperation, setCancellingLocalAiOperation] = useState(false);
  const localAiServiceStepRef = useRef<LocalAiServiceStepHandle | null>(null);

  const foundry = useFoundryWizardState();
  const gemini = useGeminiWizardState();
  const openai = useOpenAiWizardState();
  const huggingFace = useHuggingFaceWizardState();
  const localAi = useLocalAiWizardState();

  // ---------------------------------------------------------------------------
  // Snapshot-derived values (owned by orchestrator, computed from its snapshot)
  // ---------------------------------------------------------------------------

  const existingFoundryModels = useMemo(
    () => (snapshot ? toExistingFoundryModels(snapshot.models) : []),
    [snapshot]
  );
  const existingGeminiModels = useMemo(
    () => (snapshot ? toExistingGeminiModels(snapshot.models) : []),
    [snapshot]
  );
  const existingOpenAiModels = useMemo(
    () => (snapshot ? toExistingOpenAiModels(snapshot.models) : []),
    [snapshot]
  );
  const existingHuggingFaceModels = useMemo(
    () => (snapshot ? toExistingHuggingFaceModels(snapshot.models) : []),
    [snapshot]
  );
  const existingLocalModels = useMemo(
    () => (snapshot ? toExistingLocalModels(snapshot.models) : []),
    [snapshot]
  );

  const existingCatalogModelCount = snapshot?.models.length ?? 0;

  const foundryConnectionConfigured = useMemo(
    () => Boolean(snapshot?.sectionSummaries.some((s) => s.sectionName === FOUNDRY_CORE_SECTION && s.readinessStatus === 'configured')),
    [snapshot]
  );
  const geminiConnectionConfigured = useMemo(
    () => Boolean(snapshot?.sectionSummaries.some((s) => s.sectionName === GEMINI_CORE_SECTION && s.readinessStatus === 'configured')),
    [snapshot]
  );
  const openAiConnectionConfigured = useMemo(
    () => Boolean(snapshot?.sectionSummaries.some((s) => s.sectionName === OPENAI_CORE_SECTION && s.readinessStatus === 'configured')),
    [snapshot]
  );
  const huggingFaceConnectionConfigured = useMemo(
    () => Boolean(snapshot?.sectionSummaries.some((s) => s.sectionName === HUGGINGFACE_SECTION && s.readinessStatus === 'configured')),
    [snapshot]
  );

  // ---------------------------------------------------------------------------
  // Cross-provider derived values
  // ---------------------------------------------------------------------------

  const lockFoundryDraftAsGlobalDefault =
    existingCatalogModelCount === 0
    && foundry.draftModels.length === 0
    && gemini.draftModels.length === 0
    && openai.draftModels.length === 0
    && huggingFace.draftModels.length === 0;
  const effectiveSetFoundryDraftAsGlobalDefault = lockFoundryDraftAsGlobalDefault ? true : foundry.draftAsGlobalDefault;

  const lockGeminiDraftAsGlobalDefault =
    existingCatalogModelCount === 0
    && gemini.draftModels.length === 0
    && foundry.draftModels.length === 0
    && openai.draftModels.length === 0
    && huggingFace.draftModels.length === 0;
  const effectiveSetGeminiDraftAsGlobalDefault = lockGeminiDraftAsGlobalDefault ? true : gemini.draftAsGlobalDefault;

  const lockOpenAiDraftAsGlobalDefault =
    existingCatalogModelCount === 0
    && openai.draftModels.length === 0
    && foundry.draftModels.length === 0
    && gemini.draftModels.length === 0
    && huggingFace.draftModels.length === 0;
  const effectiveSetOpenAiDraftAsGlobalDefault = lockOpenAiDraftAsGlobalDefault ? true : openai.draftAsGlobalDefault;

  const lockHuggingFaceDraftAsGlobalDefault =
    existingCatalogModelCount === 0
    && huggingFace.draftModels.length === 0
    && foundry.draftModels.length === 0
    && gemini.draftModels.length === 0
    && openai.draftModels.length === 0;
  const effectiveSetHuggingFaceDraftAsGlobalDefault = lockHuggingFaceDraftAsGlobalDefault ? true : huggingFace.draftAsGlobalDefault;

  const foundryTotalModelCount = existingFoundryModels.length + foundry.draftModels.length;
  const geminiTotalModelCount = existingGeminiModels.length + gemini.draftModels.length;
  const openAiTotalModelCount = existingOpenAiModels.length + openai.draftModels.length;
  const huggingFaceTotalModelCount = existingHuggingFaceModels.length + huggingFace.draftModels.length;
  const localAiTotalModelCount = existingLocalModels.length + localAi.draftModels.filter((d) => d.asyncStatus === 'completed').length;

  const readyForBasicChat = useMemo(() => {
    if (provider === 'foundry') return foundryConnectionConfigured && existingFoundryModels.length > 0;
    if (provider === 'google-gemini') return geminiConnectionConfigured && existingGeminiModels.length > 0;
    if (provider === 'huggingface') return huggingFaceConnectionConfigured && existingHuggingFaceModels.length > 0;
    if (provider === 'local-ai') return localAiTotalModelCount > 0;
    return openAiConnectionConfigured && existingOpenAiModels.length > 0;
  }, [
    provider,
    foundryConnectionConfigured,
    geminiConnectionConfigured,
    huggingFaceConnectionConfigured,
    openAiConnectionConfigured,
    existingFoundryModels.length,
    existingGeminiModels.length,
    existingHuggingFaceModels.length,
    existingOpenAiModels.length,
    localAiTotalModelCount,
  ]);

  const finishWarnings = useMemo(() => {
    if (!snapshot) return [];
    if (provider === 'foundry') return summarizeFoundryOptionalServiceWarnings(snapshot);
    if (provider === 'google-gemini') return summarizeGeminiOptionalServiceWarnings(snapshot);
    if (provider === 'huggingface') return summarizeHuggingFaceOptionalServiceWarnings(snapshot);
    if (provider === 'local-ai') return summarizeLocalAiOptionalServiceWarnings(snapshot);
    return summarizeOpenAiOptionalServiceWarnings(snapshot);
  }, [provider, snapshot]);

  const providerLabel =
    provider === 'foundry' ? 'Microsoft Foundry'
    : provider === 'google-gemini' ? 'Google Gemini'
    : provider === 'huggingface' ? 'Hugging Face'
    : provider === 'local-ai' ? 'Local AI'
    : 'OpenAI';

  // ---------------------------------------------------------------------------
  // Snapshot loading
  // ---------------------------------------------------------------------------

  const loadServiceState = useCallback(async (serviceId: OptionalServiceKey): Promise<ServiceEditorStateDto | undefined> => {
    try {
      return await api.settings.services.get(serviceId);
    } catch {
      return undefined;
    }
  }, []);

  const loadSnapshot = useCallback(async (): Promise<WizardLoadSnapshot> => {
    const sectionNames = [
      FOUNDRY_CORE_SECTION,
      FOUNDRY_EMBEDDINGS_SECTION,
      FOUNDRY_IMAGES_SECTION,
      FOUNDRY_SPEECH_SECTION,
      FOUNDRY_DOCUMENT_INTELLIGENCE_SECTION,
      GEMINI_CORE_SECTION,
      OPENAI_CORE_SECTION,
      HUGGINGFACE_SECTION,
    ];

    const [sectionSummaries, schema, models, ...sections] = await Promise.all([
      api.settings.getSections(),
      api.settings.getSchema(),
      api.settings.getModels(),
      ...sectionNames.map((name) => api.settings.getSection(name)),
    ]);

    const serviceStatesArray = await Promise.all(OPTIONAL_SERVICE_KEYS.map((serviceId) => loadServiceState(serviceId)));
    const serviceStates: Partial<Record<OptionalServiceKey, ServiceEditorStateDto>> = {};
    OPTIONAL_SERVICE_KEYS.forEach((serviceId, index) => {
      const value = serviceStatesArray[index];
      if (value) {
        serviceStates[serviceId] = value;
      }
    });

    const sectionsByName: Record<string, SettingsSectionDto> = {};
    sections.forEach((section) => {
      sectionsByName[section.sectionName] = section;
    });

    return {
      sectionSummaries,
      sectionsByName,
      models,
      serviceStates,
      defaults: {
        azureOpenAiApiVersion: getSchemaDefault(schema, FOUNDRY_CORE_SECTION, 'ApiVersion', '2025-04-01-preview'),
        azureOpenAiImagesApiVersion: getSchemaDefault(schema, FOUNDRY_IMAGES_SECTION, 'ApiVersion', '2025-04-01-preview'),
      },
    };
  }, [loadServiceState]);

  const resetWithSnapshot = useCallback((nextSnapshot: WizardLoadSnapshot) => {
    setSnapshot(nextSnapshot);
    foundry.resetWithSnapshot(nextSnapshot);
    gemini.resetWithSnapshot(nextSnapshot);
    openai.resetWithSnapshot(nextSnapshot);
    huggingFace.resetWithSnapshot(nextSnapshot);
    localAi.resetWithSnapshot(nextSnapshot);
  }, [foundry.resetWithSnapshot, gemini.resetWithSnapshot, openai.resetWithSnapshot, huggingFace.resetWithSnapshot, localAi.resetWithSnapshot]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!isOpen) {
      return;
    }
    let cancelled = false;
    setLoading(true);
    setGlobalError(null);
    setStep('provider');
    setProvider('foundry');
    setDontAutoOpenAgain(false);

    void (async () => {
      try {
        const initialSnapshot = await loadSnapshot();
        if (cancelled) {
          return;
        }
        resetWithSnapshot(initialSnapshot);
      } catch (error) {
        if (!cancelled) {
          const message = error instanceof Error ? error.message : 'Failed to load wizard data.';
          setGlobalError(message);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [isOpen, loadSnapshot, resetWithSnapshot]);

  useEffect(() => {
    if (provider === 'local-ai' && step === 'models') {
      localAi.loadRuntimeData();
    }
  }, [provider, step]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    setLocalAiStepOperation(null);
    setCancellingLocalAiOperation(false);
  }, [provider, step]);

  // ---------------------------------------------------------------------------
  // Navigation helpers
  // ---------------------------------------------------------------------------

  const closeWizard = useCallback(() => {
    onDismiss(dontAutoOpenAgain);
  }, [dontAutoOpenAgain, onDismiss]);

  const openSettings = useCallback(() => {
    onOpenSettings(dontAutoOpenAgain);
  }, [dontAutoOpenAgain, onOpenSettings]);

  // ---------------------------------------------------------------------------
  // Persistence orchestration
  // ---------------------------------------------------------------------------

  const persistCurrentStep = useCallback(async () => {
    if (!snapshot) throw new Error('Wizard state is not loaded.');

    if (step === 'connection') {
      if (provider === 'foundry') {
        await foundry.persistConnection(snapshot, loadSnapshot, setSnapshot);
      } else if (provider === 'google-gemini') {
        await gemini.persistConnection(snapshot, loadSnapshot, setSnapshot);
      } else if (provider === 'huggingface') {
        await huggingFace.persistConnection(snapshot, loadSnapshot, setSnapshot);
      } else if (provider === 'local-ai') {
        await localAi.persistLocalAiPrereqs(snapshot, loadSnapshot, setSnapshot);
      } else {
        await openai.persistConnection(snapshot, loadSnapshot, setSnapshot);
      }
      return;
    }

    if (step === 'models') {
      if (provider === 'foundry') {
        await foundry.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
      } else if (provider === 'google-gemini') {
        await gemini.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
      } else if (provider === 'huggingface') {
        await huggingFace.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
      } else if (provider === 'local-ai') {
        await localAi.persistLocalAiModels(snapshot, loadSnapshot, setSnapshot);
      } else {
        await openai.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
      }
      return;
    }

    if (provider === 'local-ai' && isLocalAiServiceStep(step)) {
      if (!localAiServiceStepRef.current) {
        throw new Error('Local service step is not ready.');
      }
      await localAiServiceStepRef.current.persist();
      const refreshed = await loadSnapshot();
      setSnapshot(refreshed);
      return;
    }

    if (step === 'optionalServices') {
      if (provider === 'foundry') {
        await foundry.persistOptionalServices(snapshot, loadSnapshot, setSnapshot);
      } else if (provider === 'google-gemini') {
        await gemini.persistOptionalServices(snapshot, loadSnapshot, setSnapshot);
      } else if (provider === 'huggingface') {
        await huggingFace.persistOptionalServices(snapshot, loadSnapshot, setSnapshot);
      } else if (provider === 'local-ai') {
        await localAi.persistLocalAiOptionalServices(loadSnapshot, setSnapshot);
      } else {
        await openai.persistOptionalServices(snapshot, loadSnapshot, setSnapshot);
      }
    }
  }, [
    snapshot,
    step,
    provider,
    foundry,
    gemini,
    huggingFace,
    openai,
    localAi,
    loadSnapshot,
  ]);

  const handleNext = useCallback(async () => {
    if (saving) return;
    if (provider === 'local-ai' && isLocalAiServiceStep(step) && localAiStepOperation?.inFlight) return;

    setGlobalError(null);
    setSaving(true);
    try {
      await persistCurrentStep();
      setStep((previous) => nextStep(provider, previous));
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not continue to the next step.';
      setGlobalError(message);
    } finally {
      setSaving(false);
    }
  }, [saving, provider, step, localAiStepOperation?.inFlight, persistCurrentStep]);

  const handleBack = useCallback(() => {
    if (saving) return;
    if (provider === 'local-ai' && isLocalAiServiceStep(step) && localAiStepOperation?.inFlight) return;
    setGlobalError(null);
    setStep((previous) => previousStep(provider, previous));
  }, [localAiStepOperation?.inFlight, provider, saving, step]);

  const handleFinish = useCallback(async () => {
    if (saving) return;
    if (provider === 'local-ai' && isLocalAiServiceStep(step) && localAiStepOperation?.inFlight) return;

    if (step !== 'finish') {
      setGlobalError(null);
      setSaving(true);
      try {
        await persistCurrentStep();
        setStep('finish');
      } catch (error) {
        const message = error instanceof Error ? error.message : 'Could not continue to the finish step.';
        setGlobalError(message);
      } finally {
        setSaving(false);
      }
      return;
    }

    setGlobalError(null);
    setSaving(true);
    try {
      if (!snapshot) throw new Error('Wizard state is not loaded.');

      if (provider === 'foundry') {
        const hasPendingDrafts = foundry.draftModels.some((m) => !m.persisted);
        if (hasPendingDrafts) {
          await foundry.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
        }
      } else if (provider === 'google-gemini') {
        const hasPendingDrafts = gemini.draftModels.some((m) => !m.persisted);
        if (hasPendingDrafts) {
          await gemini.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
        }
      } else if (provider === 'huggingface') {
        const hasPendingDrafts = huggingFace.draftModels.some((m) => !m.persisted);
        if (hasPendingDrafts) {
          await huggingFace.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
        }
      } else if (provider === 'local-ai') {
        await localAi.persistLocalAiModels(snapshot, loadSnapshot, setSnapshot);
      } else {
        const hasPendingDrafts = openai.draftModels.some((m) => !m.persisted);
        if (hasPendingDrafts) {
          await openai.persistModels(snapshot, loadSnapshot, setSnapshot, setGlobalError);
        }
      }

      closeWizard();
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not finish setup.';
      setGlobalError(message);
    } finally {
      setSaving(false);
    }
  }, [
    closeWizard,
    foundry,
    gemini,
    huggingFace,
    openai,
    localAi,
    persistCurrentStep,
    loadSnapshot,
    snapshot,
    provider,
    saving,
    step,
    localAiStepOperation?.inFlight,
  ]);

  const localAiHasActiveDownloads = localAi.draftModels.some(
    (d) => d.asyncStatus === 'submitted' || d.asyncStatus === 'downloading'
  );
  const isCurrentLocalAiServiceStep = provider === 'local-ai' && isLocalAiServiceStep(step);
  const localAiNavigationBlockedByOperation = isCurrentLocalAiServiceStep && Boolean(localAiStepOperation?.inFlight);

  const handleSkipLocalAiService = useCallback(() => {
    if (saving || loading || !isCurrentLocalAiServiceStep || localAiNavigationBlockedByOperation) return;
    setGlobalError(null);
    setStep((previous) => nextStep(provider, previous));
  }, [isCurrentLocalAiServiceStep, loading, localAiNavigationBlockedByOperation, provider, saving]);

  const handleCancelLocalAiServiceOperation = useCallback(async () => {
    if (!localAiStepOperation || !localAiStepOperation.inFlight || cancellingLocalAiOperation) return;
    setGlobalError(null);
    setCancellingLocalAiOperation(true);
    try {
      await api.settings.localModels.cancelOperation(localAiStepOperation.serviceId, localAiStepOperation.operationId);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Could not cancel the active download operation.';
      setGlobalError(message);
    } finally {
      setCancellingLocalAiOperation(false);
    }
  }, [cancellingLocalAiOperation, localAiStepOperation]);

  const isNextDisabled = useMemo(() => {
    if (loading || saving) return true;
    if (localAiNavigationBlockedByOperation) return true;
    if (step === 'provider') return false;
    if (step === 'models') {
      if (provider === 'foundry') return foundryTotalModelCount === 0;
      if (provider === 'google-gemini') return geminiTotalModelCount === 0;
      if (provider === 'huggingface') return huggingFaceTotalModelCount === 0;
      if (provider === 'local-ai') {
        const hasNoModels = localAi.draftModels.length === 0 && existingLocalModels.length === 0;
        return hasNoModels || localAiHasActiveDownloads;
      }
      return openAiTotalModelCount === 0;
    }
    if (step === 'finish') return true;
    return false;
  }, [
    foundryTotalModelCount,
    geminiTotalModelCount,
    huggingFaceTotalModelCount,
    openAiTotalModelCount,
    localAi.draftModels.length,
    existingLocalModels.length,
    localAiHasActiveDownloads,
    localAiNavigationBlockedByOperation,
    loading,
    provider,
    saving,
    step,
  ]);

  const isFinishDisabled = useMemo(() => {
    if (loading || saving) return true;
    if (localAiNavigationBlockedByOperation) return true;
    if (provider === 'foundry') return foundryTotalModelCount === 0;
    if (provider === 'google-gemini') return geminiTotalModelCount === 0;
    if (provider === 'huggingface') return huggingFaceTotalModelCount === 0;
    if (provider === 'local-ai') {
      const hasNoModels = localAi.draftModels.length === 0 && existingLocalModels.length === 0;
      return hasNoModels || localAiHasActiveDownloads;
    }
    return openAiTotalModelCount === 0;
  }, [
    foundryTotalModelCount,
    geminiTotalModelCount,
    huggingFaceTotalModelCount,
    openAiTotalModelCount,
    localAi.draftModels.length,
    existingLocalModels.length,
    localAiHasActiveDownloads,
    localAiNavigationBlockedByOperation,
    loading,
    provider,
    saving,
  ]);

  const activeWizardSteps = STEP_TABS_BY_PROVIDER[provider];
  const currentStepLabel = useMemo(() => {
    const index = activeWizardSteps.findIndex((item) => item.id === step);
    const position = index >= 0 ? index + 1 : 1;
    return `${position} of ${activeWizardSteps.length}`;
  }, [activeWizardSteps, step]);

  // ---------------------------------------------------------------------------
  // Per-provider other-draft counts (for addDraftModel cross-provider logic)
  // ---------------------------------------------------------------------------

  const nonFoundryDraftCount = gemini.draftModels.length + openai.draftModels.length + huggingFace.draftModels.length;
  const nonGeminiDraftCount = foundry.draftModels.length + openai.draftModels.length + huggingFace.draftModels.length;
  const nonOpenAiDraftCount = foundry.draftModels.length + gemini.draftModels.length + huggingFace.draftModels.length;
  const nonHuggingFaceDraftCount = foundry.draftModels.length + gemini.draftModels.length + openai.draftModels.length;

  return (
    <SettingsModal
      isOpen={isOpen}
      title="Add AI Services Wizard"
      onClose={closeWizard}
      maxWidthClass="max-w-4xl"
      disableDismiss={saving}
      disableOverlayDismiss
      footer={(
        <div className="flex w-full flex-wrap items-center justify-between gap-2">
          <div className="space-y-1">
            <label className="inline-flex items-center gap-2 text-xs text-gray-700">
              <input
                type="checkbox"
                className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                checked={dontAutoOpenAgain}
                onChange={(event) => setDontAutoOpenAgain(event.target.checked)}
              />
              Don&apos;t auto-open this again on this device
            </label>
            {localAiNavigationBlockedByOperation ? (
              <div className="flex flex-wrap items-center gap-2 text-xs text-amber-800">
                <span>
                  Download in progress ({localAiStepOperation?.status ?? 'running'}). Navigation is blocked until it completes or is cancelled.
                </span>
                <button
                  type="button"
                  onClick={() => void handleCancelLocalAiServiceOperation()}
                  disabled={cancellingLocalAiOperation}
                  className="rounded border border-amber-300 bg-amber-100 px-2 py-1 font-medium text-amber-900 hover:bg-amber-200 disabled:opacity-50"
                >
                  {cancellingLocalAiOperation ? 'Cancelling…' : 'Cancel operation'}
                </button>
              </div>
            ) : null}
          </div>
          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={closeWizard}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
            >
              Not now
            </button>
            <button
              type="button"
              onClick={openSettings}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
            >
              Configure manually
            </button>
            <button
              type="button"
              onClick={handleBack}
              disabled={saving || step === 'provider' || localAiNavigationBlockedByOperation}
              className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
            >
              Back
            </button>
            {isCurrentLocalAiServiceStep ? (
              <button
                type="button"
                onClick={handleSkipLocalAiService}
                disabled={saving || loading || localAiNavigationBlockedByOperation}
                className="rounded border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              >
                Skip this service
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => void handleNext()}
              disabled={isNextDisabled}
              className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:bg-blue-400"
            >
              {saving ? 'Saving...' : 'Next'}
            </button>
            <button
              type="button"
              onClick={() => void handleFinish()}
              disabled={isFinishDisabled}
              className="rounded bg-emerald-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-emerald-700 disabled:bg-emerald-300"
            >
              {saving ? 'Saving...' : 'Finish'}
            </button>
          </div>
        </div>
      )}
    >
      <div className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="text-xs text-gray-500">Step {currentStepLabel}</div>
          <div className="flex flex-wrap gap-2 text-xs">
            {activeWizardSteps.map((item) => (
              <span
                key={item.id}
                className={`rounded-full px-2 py-0.5 ${
                  item.id === step ? 'bg-blue-100 text-blue-800' : 'bg-gray-100 text-gray-500'
                }`}
              >
                {item.label}
              </span>
            ))}
          </div>
        </div>

        {loading ? <p className="text-sm text-gray-600">Loading wizard data...</p> : null}
        {globalError ? <div className="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{globalError}</div> : null}

        {!loading && step === 'provider' ? (
          <ProviderStep value={provider} onChange={setProvider} />
        ) : null}

        {!loading && step === 'connection' ? (
          provider === 'foundry' ? (
            <CoreConnectionStep
              resource={foundry.coreForm.resource}
              apiKey={foundry.coreForm.apiKey}
              apiVersion={foundry.coreForm.apiVersion}
              apiKeyHasStoredValue={foundry.coreForm.apiKeyHasStoredValue}
              errors={foundry.coreErrors}
              onChange={foundry.setCoreForm}
            />
          ) : provider === 'google-gemini' ? (
            <GeminiConnectionStep
              apiKey={gemini.coreForm.apiKey}
              apiKeyHasStoredValue={gemini.coreForm.apiKeyHasStoredValue}
              errors={gemini.coreErrors}
              onChange={gemini.setCoreForm}
            />
          ) : provider === 'huggingface' ? (
            <HuggingFaceConnectionStep
              token={huggingFace.coreForm.token}
              routerBaseUrl={huggingFace.coreForm.routerBaseUrl}
              tokenHasStoredValue={huggingFace.coreForm.tokenHasStoredValue}
              errors={huggingFace.coreErrors}
              onChange={huggingFace.setCoreForm}
            />
          ) : provider === 'local-ai' ? (
            <LocalAiPrerequisitesStep
              value={localAi.prereqsForm}
              errors={localAi.prereqsErrors}
              onChange={localAi.setPrereqsForm}
            />
          ) : (
            <OpenAiConnectionStep
              apiKey={openai.coreForm.apiKey}
              endpoint={openai.coreForm.endpoint}
              apiKeyHasStoredValue={openai.coreForm.apiKeyHasStoredValue}
              errors={openai.coreErrors}
              onChange={openai.setCoreForm}
            />
          )
        ) : null}

        {!loading && step === 'models' ? (
          provider === 'foundry' ? (
            <ModelsStep
              existingModels={existingFoundryModels}
              draftModels={foundry.draftModels}
              draftModelId={foundry.draftModelId}
              draftProvider={foundry.draftModelProvider}
              setDraftAsGlobalDefault={effectiveSetFoundryDraftAsGlobalDefault}
              lockDraftAsGlobalDefault={lockFoundryDraftAsGlobalDefault}
              addError={foundry.modelAddError}
              validationError={foundry.modelStepError}
              onDraftModelIdChange={foundry.setDraftModelId}
              onDraftProviderChange={foundry.setDraftModelProvider}
              onSetDraftAsGlobalDefaultChange={foundry.setDraftAsGlobalDefault}
              onAddModel={() => snapshot && foundry.addDraftModel(snapshot, existingCatalogModelCount, nonFoundryDraftCount)}
              onRemoveDraftModel={foundry.removeDraftModel}
            />
          ) : provider === 'google-gemini' ? (
            <GeminiModelsStep
              existingModels={existingGeminiModels}
              draftModels={gemini.draftModels}
              draftModelId={gemini.draftModelId}
              setDraftAsGlobalDefault={effectiveSetGeminiDraftAsGlobalDefault}
              lockDraftAsGlobalDefault={lockGeminiDraftAsGlobalDefault}
              addError={gemini.modelAddError}
              validationError={gemini.modelStepError}
              onDraftModelIdChange={gemini.setDraftModelId}
              onSetDraftAsGlobalDefaultChange={gemini.setDraftAsGlobalDefault}
              onAddModel={() => snapshot && gemini.addDraftModel(snapshot, existingCatalogModelCount, nonGeminiDraftCount)}
              onRemoveDraftModel={gemini.removeDraftModel}
            />
          ) : provider === 'huggingface' ? (
            <HuggingFaceModelsStep
              existingModels={existingHuggingFaceModels}
              draftModels={huggingFace.draftModels}
              draftModelId={huggingFace.draftModelId}
              setDraftAsGlobalDefault={effectiveSetHuggingFaceDraftAsGlobalDefault}
              lockDraftAsGlobalDefault={lockHuggingFaceDraftAsGlobalDefault}
              addError={huggingFace.modelAddError}
              validationError={huggingFace.modelStepError}
              onDraftModelIdChange={huggingFace.setDraftModelId}
              onSetDraftAsGlobalDefaultChange={huggingFace.setDraftAsGlobalDefault}
              onAddModel={() => snapshot && huggingFace.addDraftModel(snapshot, existingCatalogModelCount, nonHuggingFaceDraftCount)}
              onRemoveDraftModel={huggingFace.removeDraftModel}
            />
          ) : provider === 'local-ai' ? (
            <LocalAiModelsStep
              draftModels={localAi.draftModels}
              existingModels={existingLocalModels}
              profiles={localAi.profiles}
              profilesLoading={localAi.profilesLoading}
              inventory={localAi.inventory}
              inventoryLoading={localAi.inventoryLoading}
              installError={localAi.installError}
              installModelError={localAi.installModelError}
              onInstall={localAi.startInstall}
              onRemoveDraft={localAi.removeDraftModel}
            />
          ) : (
            <OpenAiModelsStep
              existingModels={existingOpenAiModels}
              draftModels={openai.draftModels}
              draftModelId={openai.draftModelId}
              draftProvider={openai.draftModelProvider}
              setDraftAsGlobalDefault={effectiveSetOpenAiDraftAsGlobalDefault}
              lockDraftAsGlobalDefault={lockOpenAiDraftAsGlobalDefault}
              addError={openai.modelAddError}
              validationError={openai.modelStepError}
              onDraftModelIdChange={openai.setDraftModelId}
              onDraftProviderChange={openai.setDraftModelProvider}
              onSetDraftAsGlobalDefaultChange={openai.setDraftAsGlobalDefault}
              onAddModel={() => snapshot && openai.addDraftModel(snapshot, existingCatalogModelCount, nonOpenAiDraftCount)}
              onRemoveDraftModel={openai.removeDraftModel}
            />
          )
        ) : null}

        {!loading && step === 'localAiSpeechTranscription' ? (
          <LocalAiSpeechTranscriptionStep
            ref={localAiServiceStepRef}
            onDownloadOperationChange={setLocalAiStepOperation}
          />
        ) : null}

        {!loading && step === 'localAiImageGeneration' ? (
          <LocalAiImageGenerationStep
            ref={localAiServiceStepRef}
            onDownloadOperationChange={setLocalAiStepOperation}
          />
        ) : null}

        {!loading && step === 'localAiSpeechSynthesis' ? (
          <LocalAiSpeechSynthesisStep
            ref={localAiServiceStepRef}
            onDownloadOperationChange={setLocalAiStepOperation}
          />
        ) : null}

        {!loading && step === 'localAiDocumentIntelligence' ? (
          <LocalAiDocumentIntelligenceStep ref={localAiServiceStepRef} />
        ) : null}

        {!loading && step === 'localAiEmbeddings' ? (
          <LocalAiEmbeddingsStep
            ref={localAiServiceStepRef}
            onDownloadOperationChange={setLocalAiStepOperation}
          />
        ) : null}

        {!loading && step === 'optionalServices' ? (
          provider === 'foundry' ? (
            <OptionalServicesStep
              value={foundry.optionalForm}
              derivedCoreEndpoint={foundry.derivedCoreEndpoint}
              errors={foundry.optionalErrors}
              onChange={foundry.setOptionalForm}
            />
          ) : provider === 'google-gemini' ? (
            <GeminiOptionalServicesStep
              value={gemini.optionalForm}
              errors={gemini.optionalErrors}
              onChange={gemini.setOptionalForm}
            />
          ) : provider === 'huggingface' ? (
            <HuggingFaceOptionalServicesStep
              value={huggingFace.optionalForm}
              errors={huggingFace.optionalErrors}
              onChange={huggingFace.setOptionalForm}
            />
          ) : (
            <OpenAiOptionalServicesStep
              value={openai.optionalForm}
              errors={openai.optionalErrors}
              onChange={openai.setOptionalForm}
            />
          )
        ) : null}

        {!loading && step === 'finish' && provider === 'local-ai' && localAi.draftModels.length > 0 ? (
          <div className="rounded border border-gray-200 bg-gray-50 p-3">
            <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-gray-600">Model installation status</div>
            <div className="divide-y divide-gray-100">
              {localAi.draftModels.map((draft) => (
                <div key={draft.localId} className="py-1.5">
                  <div className="font-mono text-xs text-gray-800">{draft.catalogModelId || draft.routerModelId}</div>
                  <DraftProgress draft={draft} />
                </div>
              ))}
            </div>
          </div>
        ) : null}

        {!loading && step === 'finish' ? (
          <FinishStep
            providerLabel={providerLabel}
            readyForBasicChat={readyForBasicChat}
            totalModelCount={
              provider === 'foundry'
                ? existingFoundryModels.length
                : provider === 'google-gemini'
                  ? existingGeminiModels.length
                  : provider === 'huggingface'
                    ? existingHuggingFaceModels.length
                    : provider === 'local-ai'
                      ? localAiTotalModelCount
                      : existingOpenAiModels.length
            }
            warningItems={finishWarnings}
          />
        ) : null}
      </div>
    </SettingsModal>
  );
}
