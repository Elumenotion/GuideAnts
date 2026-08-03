import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { api } from '../../../../services/api';
import { createLocalModelOnboardingPoller } from '../../../../features/localModelOnboarding/useOperationPolling';
import {
  HUGGINGFACE_SECTION,
  LOCAL_AI_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
  WIZARD_DEFER_WARMUP_OPTIONS,
} from '../constants';
import { useLocalAiWizardState } from '../useLocalAiWizardState';
import {
  createLoadSnapshot,
  createProvider,
  createSection,
  createServiceState,
  createSetSnapshot,
  createWizardSnapshot,
} from './wizardHookTestHelpers';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getSections: vi.fn(),
      getSection: vi.fn(),
      updateSection: vi.fn(),
      getModels: vi.fn(),
      addModel: vi.fn(),
      getLlamaInventory: vi.fn(),
      chatDefaults: {
        get: vi.fn(),
        update: vi.fn(),
      },
      services: {
        updateProviderFields: vi.fn(),
        updateActiveProvider: vi.fn(),
      },
      getDownloadStatus: vi.fn(),
    },
  },
}));

vi.mock('../../../../features/localModelOnboarding/useOperationPolling', () => ({
  createLocalModelOnboardingPoller: vi.fn(),
}));

describe('useLocalAiWizardState', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getLlamaInventory).mockResolvedValue([
      {
        routerModelId: 'qwen3-local',
        runtimeState: 'unloaded',
        hasModelFile: true,
        hasMmprojFile: false,
        catalogModelIds: [],
        notebookReferenceCount: 0,
        modelPath: '/models/qwen3.gguf',
        mmprojPath: null,
      },
    ] as never);
    vi.mocked(api.settings.updateSection).mockImplementation(async (sectionName, request) => ({
      ...createSection(sectionName),
      rowVersion: '2',
      payload: request.payload,
      secretHasValue: { Token: true },
    }));
    vi.mocked(api.settings.addModel).mockResolvedValue({
      addOperation: { kind: 'sync', status: 'completed' },
    } as never);
    vi.mocked(api.settings.chatDefaults.get).mockResolvedValue({
      rowVersion: '1',
      defaultModelId: null,
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });
    vi.mocked(api.settings.chatDefaults.update).mockResolvedValue({
      rowVersion: '2',
      defaultModelId: 'qwen3-local',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });
    vi.mocked(api.settings.services.updateProviderFields).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.services.updateActiveProvider).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.getDownloadStatus).mockResolvedValue({
      operationId: 'op-1',
      status: 'completed',
      progress: 100,
      errorMessage: null,
    } as never);
  });

  it('loads local runtime inventory', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.loadRuntimeData();
    });

    await waitFor(() => expect(result.current.inventoryLoading).toBe(false));
    expect(result.current.inventory).toHaveLength(1);
  });

  it('validates optional service timeouts', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableEmbeddings: true,
        embeddingsTimeoutSeconds: '0',
      });
    });

    let valid = true;
    act(() => {
      valid = result.current.validateLocalAiOptionalServices();
    });
    expect(valid).toBe(false);
    await waitFor(() => {
      expect(result.current.optionalErrors.embeddingsTimeoutSeconds).toBe('Timeout must be a positive integer.');
    });
  });

  it('persists optional local services when validation passes', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableEmbeddings: true,
        embeddingsTimeoutSeconds: '300',
        embeddingsLocalMinIntervalMs: '100',
        enableDocumentIntelligence: true,
        documentIntelligenceTimeoutSeconds: '600',
        documentIntelligenceMaxConcurrentConversions: '2',
        documentIntelligenceAsyncStatusPollIntervalMs: '2000',
      });
    });

    await act(async () => {
      await result.current.persistLocalAiOptionalServices(createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'Embeddings',
      LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
      expect.objectContaining({
        TimeoutSeconds: '300',
        LocalMinIntervalMs: '100',
      })
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'DocumentIntelligence',
      LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence,
      WIZARD_DEFER_WARMUP_OPTIONS
    );
  });

  it('persists hugging face prerequisites when token is provided', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [HUGGINGFACE_SECTION]: createSection(HUGGINGFACE_SECTION, { Token: '' }),
      },
    });
    const refreshed = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setPrereqsForm({ huggingFaceToken: 'hf-secret-token-12345' });
    });

    await act(async () => {
      await result.current.persistLocalAiPrereqs(snapshot, createLoadSnapshot(refreshed), createSetSnapshot());
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      HUGGINGFACE_SECTION,
      expect.objectContaining({
        payload: { Token: 'hf-secret-token-12345' },
      })
    );
    expect(result.current.prereqsForm.huggingFaceTokenHasStoredValue).toBe(true);
  });

  it('rejects model step when no local models are available', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await expect(
        result.current.persistLocalAiModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('No models.');
    });
    expect(result.current.modelStepError).toBe('Install at least one local AI model to continue.');
  });

  it('handles local runtime inventory load failures gracefully', async () => {
    vi.mocked(api.settings.getLlamaInventory).mockRejectedValueOnce(new Error('inventory down'));

    const { result } = renderHook(() => useLocalAiWizardState());
    act(() => {
      result.current.loadRuntimeData();
    });

    await waitFor(() => {
      expect(result.current.inventoryLoading).toBe(false);
    });
    expect(result.current.inventory).toHaveLength(0);
  });

  it('resets optional service form from active snapshot services', () => {
    const embId = LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings;
    const snapshot = createWizardSnapshot({
      serviceStates: {
        Embeddings: {
          ...createServiceState('Embeddings', embId),
          activeProviderId: embId,
          providers: [
            {
              ...createProvider(embId),
              fields: {
                TimeoutSeconds: { value: '450' },
                LocalMinIntervalMs: { value: '250' },
              },
            },
          ],
        },
        ImageGeneration: createServiceState('ImageGeneration', LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration),
        SpeechTranscription: createServiceState('SpeechTranscription', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription),
        SpeechSynthesis: createServiceState('SpeechSynthesis', LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis),
        DocumentIntelligence: createServiceState('DocumentIntelligence', LOCAL_AI_SERVICE_PROVIDER_IDS.DocumentIntelligence),
      },
    });
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.resetWithSnapshot(snapshot);
    });

    expect(result.current.optionalForm.enableEmbeddings).toBe(true);
    expect(result.current.optionalForm.embeddingsTimeoutSeconds).toBe('450');
    expect(result.current.optionalForm.embeddingsLocalMinIntervalMs).toBe('250');
    expect(result.current.prereqsForm.huggingFaceTokenHasStoredValue).toBe(false);
  });

  it('validates document intelligence optional fields', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableDocumentIntelligence: true,
        documentIntelligenceTimeoutSeconds: '0',
        documentIntelligenceMaxConcurrentConversions: '-1',
        documentIntelligenceAsyncStatusPollIntervalMs: 'abc',
      });
    });

    let valid = true;
    act(() => {
      valid = result.current.validateLocalAiOptionalServices();
    });
    expect(valid).toBe(false);
    await waitFor(() => {
      expect(result.current.optionalErrors.documentIntelligenceTimeoutSeconds).toBeTruthy();
      expect(result.current.optionalErrors.documentIntelligenceMaxConcurrentConversions).toBeTruthy();
      expect(result.current.optionalErrors.documentIntelligenceAsyncStatusPollIntervalMs).toBeTruthy();
    });
  });

  it('persists images optional service with output format', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableImages: true,
        imagesTimeoutSeconds: '500',
        imagesLocalOutputFormat: 'png',
      });
    });

    await act(async () => {
      await result.current.persistLocalAiOptionalServices(createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'ImageGeneration',
      LOCAL_AI_SERVICE_PROVIDER_IDS.ImageGeneration,
      expect.objectContaining({
        TimeoutSeconds: '500',
        LocalOutputFormat: 'png',
      }),
    );
  });

  it('rejects model step while downloads are in progress', async () => {
    const snapshot = createWizardSnapshot();
    vi.mocked(api.settings.addModel).mockResolvedValueOnce({
      operationId: 'download-op-1',
    } as never);

    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: false,
      });
    });

    await waitFor(() => {
      expect(result.current.draftModels[0]?.asyncOperationId).toBe('download-op-1');
    });

    await act(async () => {
      await expect(
        result.current.persistLocalAiModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot()),
      ).rejects.toThrow('Downloads in progress.');
    });
    expect(result.current.modelStepError).toContain('downloads are in progress');
  });

  it('parses structured add-model errors during install', async () => {
    const structuredError = Object.assign(new Error('Conflict'), {
      body: {
        code: 'MODEL_EXISTS',
        message: 'Model already installed.',
      },
    });
    vi.mocked(api.settings.addModel).mockRejectedValueOnce(structuredError);

    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: false,
      });
    });

    expect(result.current.installModelError?.code).toBe('MODEL_EXISTS');
    expect(result.current.draftModels[0]?.asyncStatus).toBe('error');
  });

  it('installs a local model synchronously via existing alias', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: true,
      });
    });

    expect(api.settings.addModel).toHaveBeenCalledWith(
      expect.objectContaining({
        provider: 'llama-cpp',
        catalog: expect.objectContaining({ modelId: 'qwen3-local' }),
      })
    );
    await waitFor(() => expect(result.current.draftModels[0]?.asyncStatus).toBe('completed'));
    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
      expect.objectContaining({ defaultModelId: 'qwen3-local' })
    );
  });

  it('validates negative embeddings min interval', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableEmbeddings: true,
        embeddingsTimeoutSeconds: '300',
        embeddingsLocalMinIntervalMs: '-1',
      });
    });

    let valid = true;
    act(() => {
      valid = result.current.validateLocalAiOptionalServices();
    });
    expect(valid).toBe(false);
    expect(result.current.optionalErrors.embeddingsLocalMinIntervalMs).toBe(
      'Min interval must be a non-negative integer.',
    );
  });

  it('persists speech optional services when enabled', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableSpeechTranscription: true,
        speechTranscriptionTimeoutSeconds: '240',
        enableSpeechSynthesis: true,
        speechSynthesisTimeoutSeconds: '180',
      });
    });

    await act(async () => {
      await result.current.persistLocalAiOptionalServices(createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechTranscription',
      LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechTranscription,
      WIZARD_DEFER_WARMUP_OPTIONS
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechSynthesis',
      LOCAL_AI_SERVICE_PROVIDER_IDS.SpeechSynthesis,
      WIZARD_DEFER_WARMUP_OPTIONS
    );
  });

  it('skips prerequisite persistence when hugging face section is missing', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [HUGGINGFACE_SECTION]: undefined as never,
      },
    });
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.persistLocalAiPrereqs(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.updateSection).not.toHaveBeenCalled();
  });

  it('marks draft model ready when existing local models are present', async () => {
    const snapshot = createWizardSnapshot({
      models: [
        {
          modelId: 'qwen3-local',
          displayName: 'Qwen 3 Local',
          provider: 'llama-cpp',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.resetWithSnapshot(snapshot);
    });

    await act(async () => {
      await result.current.persistLocalAiModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });
    expect(result.current.modelStepError).toBeNull();
    expect(result.current.readyForBasicChat).toBe(true);
  });

  it('validates speech optional service timeouts', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableSpeechTranscription: true,
        speechTranscriptionTimeoutSeconds: '0',
        enableSpeechSynthesis: true,
        speechSynthesisTimeoutSeconds: 'abc',
      });
    });

    let valid = true;
    act(() => {
      valid = result.current.validateLocalAiOptionalServices();
    });
    expect(valid).toBe(false);
    expect(result.current.optionalErrors.speechTranscriptionTimeoutSeconds).toBeTruthy();
    expect(result.current.optionalErrors.speechSynthesisTimeoutSeconds).toBeTruthy();
  });

  it('removes draft model and stops polling', async () => {
    vi.mocked(api.settings.addModel).mockResolvedValueOnce({ operationId: 'op-1' } as never);
    vi.mocked(createLocalModelOnboardingPoller).mockReturnValue(42 as never);
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: false,
      });
    });

    const localId = result.current.draftModels[0]?.localId;
    expect(localId).toBeTruthy();

    act(() => {
      result.current.removeDraftModel(localId!);
    });
    expect(result.current.draftModels).toHaveLength(0);
  });

  it('stores generic install errors when add model fails without structured body', async () => {
    vi.mocked(api.settings.addModel).mockRejectedValueOnce(new Error('network down'));
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: false,
      });
    });

    expect(result.current.installError).toBe('network down');
    expect(result.current.draftModels[0]?.asyncStatus).toBe('error');
  });

  it('rejects model step when drafts exist but none completed successfully', async () => {
    vi.mocked(api.settings.addModel).mockRejectedValueOnce(new Error('install failed'));
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'bad-model',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'bad-model',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'bad-model',
        catalogDisplayName: 'Bad',
        setAsGlobalDefault: false,
      });
    });

    await act(async () => {
      await expect(
        result.current.persistLocalAiModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot()),
      ).rejects.toThrow('No usable models.');
    });
    expect(result.current.modelStepError).toContain('No models were installed successfully');
  });

  it('persists hugging face token using stored secret mask when field is empty', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [HUGGINGFACE_SECTION]: createSection(HUGGINGFACE_SECTION, { Token: '' }, { Token: true }),
      },
    });
    const refreshed = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setPrereqsForm({ huggingFaceToken: '', huggingFaceTokenHasStoredValue: true });
    });

    await act(async () => {
      await result.current.persistLocalAiPrereqs(snapshot, createLoadSnapshot(refreshed), createSetSnapshot());
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      HUGGINGFACE_SECTION,
      expect.objectContaining({
        payload: { Token: SECRET_MASK },
      }),
    );
  });

  it('marks draft errored when polling exceeds failure threshold', async () => {
    vi.mocked(api.settings.addModel).mockResolvedValueOnce({ operationId: 'op-fail' } as never);
    vi.mocked(createLocalModelOnboardingPoller).mockImplementation(({ onPollFailureThreshold }) => {
      onPollFailureThreshold?.();
      return 100 as never;
    });

    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: false,
      });
    });

    expect(result.current.draftModels[0]?.asyncStatus).toBe('error');
    expect(result.current.draftModels[0]?.asyncError).toContain('no longer reachable');
  });

  it('skips prerequisite persistence when token is empty and not stored', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [HUGGINGFACE_SECTION]: createSection(HUGGINGFACE_SECTION, { Token: '' }),
      },
    });
    const { result } = renderHook(() => useLocalAiWizardState());

    await act(async () => {
      await result.current.persistLocalAiPrereqs(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.updateSection).not.toHaveBeenCalled();
  });

  it('validates image generation timeout when images are enabled', () => {
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableImages: true,
        imagesTimeoutSeconds: '0',
      });
    });

    let valid = true;
    act(() => {
      valid = result.current.validateLocalAiOptionalServices();
    });

    expect(valid).toBe(false);
    expect(result.current.optionalErrors.imagesTimeoutSeconds).toBe('Timeout must be a positive integer.');
  });

  it('throws when optional service persistence is requested with invalid inputs', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useLocalAiWizardState());

    act(() => {
      result.current.setOptionalForm({
        enableEmbeddings: true,
        embeddingsTimeoutSeconds: '0',
      });
    });

    await expect(
      act(async () => {
        await result.current.persistLocalAiOptionalServices(createLoadSnapshot(snapshot), createSetSnapshot());
      }),
    ).rejects.toThrow('Optional service inputs are incomplete.');
  });

  it('stores unknown error text when sync default persistence fails with non-Error', async () => {
    vi.mocked(api.settings.chatDefaults.update).mockRejectedValueOnce('plain failure');

    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: true,
      });
    });

    expect(result.current.installError).toContain('Unknown error.');
  });

  it('retries default persistence without reasoning effort when API rejects it', async () => {
    const reasoningError = Object.assign(new Error('validation failed'), {
      body: { errors: ['ReasoningEffort is not supported'] },
    });
    vi.mocked(api.settings.chatDefaults.update)
      .mockRejectedValueOnce(reasoningError)
      .mockResolvedValueOnce({
        rowVersion: '3',
        defaultModelId: 'qwen3-local',
        overrideAllChatModels: false,
        temperature: null,
        topP: null,
        reasoningEffort: null,
        samplingParametersJson: null,
      });

    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: true,
      });
    });

    expect(api.settings.chatDefaults.update).toHaveBeenCalledTimes(2);
    expect(result.current.installError).toBeNull();
  });

  it('updates draft progress while async download polling reports status', async () => {
    vi.mocked(createLocalModelOnboardingPoller).mockImplementation(({ onUpdate }) => {
      onUpdate?.({
        operationId: 'op-progress',
        status: 'running',
        progress: 55,
        errorMessage: null,
      });
      return 77 as never;
    });
    vi.mocked(api.settings.addModel).mockResolvedValueOnce({ operationId: 'op-progress' } as never);

    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: false,
      });
    });

    expect(result.current.draftModels[0]?.asyncProgress).toBe(55);
    expect(result.current.draftModels[0]?.asyncStatus).toBe('downloading');
  });

  it('stores validation error when install form cannot be built', async () => {
    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'huggingFace',
        routerModelId: '',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: '',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: '',
        catalogDisplayName: '',
        setAsGlobalDefault: false,
      });
    });

    expect(result.current.installError).toBeTruthy();
    expect(result.current.draftModels).toHaveLength(0);
  });

  it('handles async download completion and default persistence failures', async () => {
    vi.mocked(createLocalModelOnboardingPoller).mockImplementation(({ onTerminal }) => {
      onTerminal?.({
        operationId: 'op-1',
        status: 'completed',
        progress: 100,
        errorMessage: null,
      });
      return 99 as never;
    });
    vi.mocked(api.settings.addModel).mockResolvedValueOnce({ operationId: 'op-1' } as never);
    vi.mocked(api.settings.chatDefaults.update).mockRejectedValueOnce(new Error('defaults failed'));

    const { result } = renderHook(() => useLocalAiWizardState());
    await act(async () => {
      await result.current.startInstall({
        installSource: 'existingAlias',
        routerModelId: 'qwen3-local',
        samplingParametersJson: '{}',
        reasoningChoicesJson: '',
        thinkingControlJson: '{}',
        requestFieldsWhenToolsPresentJson: '{}',
        combineSystemAndDeveloperMessages: true,
        thoughtBlockPattern: '',
        huggingFaceRepository: '',
        huggingFaceQuantIncludePattern: '',
        huggingFaceMmprojIncludePattern: '',
        huggingFaceTargetDirectory: '',
        existingAliasRouterModelId: 'qwen3-local',
        routerContextSize: '',
        routerCacheRamMib: '',
        catalogModelId: 'qwen3-local',
        catalogDisplayName: 'Qwen 3 Local',
        setAsGlobalDefault: true,
      });
    });

    await waitFor(() => {
      expect(result.current.installError).toContain('as global default failed');
    });
  });
});
