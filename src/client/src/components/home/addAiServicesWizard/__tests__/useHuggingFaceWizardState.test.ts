import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { api } from '../../../../services/api';
import { HUGGINGFACE_SECTION, HUGGINGFACE_SERVICE_PROVIDER_IDS } from '../constants';
import { useHuggingFaceWizardState } from '../useHuggingFaceWizardState';
import {
  createLoadSnapshot,
  createSection,
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
      chatDefaults: {
        get: vi.fn(),
        update: vi.fn(),
      },
      services: {
        updateProviderFields: vi.fn(),
        updateActiveProvider: vi.fn(),
      },
    },
  },
}));

describe('useHuggingFaceWizardState', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getSection).mockImplementation(async (sectionName: string) =>
      createSection(
        sectionName,
        sectionName === HUGGINGFACE_SECTION
          ? { Token: '', RouterBaseUrl: 'https://router.huggingface.co/v1' }
          : {}
      )
    );
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
      defaultModelId: 'zai-org/GLM-5.2',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });
    vi.mocked(api.settings.services.updateProviderFields).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.services.updateActiveProvider).mockResolvedValue(undefined as never);
  });

  it('rejects adding duplicate draft models', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setDraftModelId('zai-org/GLM-5.2');
      result.current.addDraftModel(snapshot, 0, 0);
    });
    act(() => {
      result.current.setDraftModelId('zai-org/GLM-5.2');
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.modelAddError).toContain('already queued');
  });

  it('rejects connection persistence when token is missing', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.token).toBe('Token is required.');
  });

  it('persists connection details with router base url', async () => {
    const snapshot = createWizardSnapshot();
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setCoreForm({
        token: 'hf-secret-token-12345',
        routerBaseUrl: 'https://router.huggingface.co/v1',
      });
    });

    await act(async () => {
      await result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), setSnapshot);
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      HUGGINGFACE_SECTION,
      expect.objectContaining({
        payload: expect.objectContaining({
          Token: 'hf-secret-token-12345',
          RouterBaseUrl: 'https://router.huggingface.co/v1',
        }),
      })
    );
    expect(result.current.coreForm.tokenHasStoredValue).toBe(true);
  });

  it('queues and persists hugging face chat models', async () => {
    const snapshot = createWizardSnapshot();
    const refreshed = createWizardSnapshot({
      models: [
        {
          modelId: 'zai-org/GLM-5.2',
          displayName: 'zai-org/GLM-5.2',
          provider: 'hf-inference-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setDraftModelId('zai-org/GLM-5.2');
      result.current.addDraftModel(snapshot, 0, 0);
    });

    await act(async () => {
      await result.current.persistModels(snapshot, createLoadSnapshot(refreshed), createSetSnapshot());
    });

    expect(api.settings.addModel).toHaveBeenCalledWith(
      expect.objectContaining({
        provider: 'hf-inference-chat',
        catalog: expect.objectContaining({ modelId: 'zai-org/GLM-5.2' }),
      })
    );
  });

  it('validates image model fields before optional service persistence', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setOptionalForm({ imagesImageToImageModelId: '' });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.imagesImageToImageModelId).toBe('Image-to-image model id is required.');
  });

  it('rejects duplicate queued model ids', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setDraftModelId('hf-custom-model');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });
    act(() => {
      result.current.setDraftModelId('hf-custom-model');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.modelAddError).toContain('already queued');
  });

  it('rejects optional service persistence when enabled embeddings timeout is invalid', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setOptionalForm({
        ...result.current.optionalForm,
        enableEmbeddings: true,
        embeddingsModelId: 'sentence-transformers/all-MiniLM-L6-v2',
        embeddingsTimeoutSeconds: '0',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.embeddingsTimeoutSeconds).toContain('positive integer');
  });

  it('rejects optional service persistence when enabled speech synthesis model id is blank', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setOptionalForm({
        ...result.current.optionalForm,
        enableSpeechSynthesis: true,
        speechSynthesisModelId: '   ',
        speechSynthesisTimeoutSeconds: '30',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.speechSynthesisModelId).toContain('TTS model id is required');
  });

  it('persists enabled optional services', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    await act(async () => {
      await result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'ImageGeneration',
      HUGGINGFACE_SERVICE_PROVIDER_IDS.ImageGeneration,
      expect.objectContaining({
        TextToImageModelId: 'Tongyi-MAI/Z-Image-Turbo',
        ImageToImageModelId: 'black-forest-labs/FLUX.2-dev',
      })
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechSynthesis',
      HUGGINGFACE_SERVICE_PROVIDER_IDS.SpeechSynthesis
    );
  });

  it('validates image model fields when images are enabled', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setOptionalForm({
        ...result.current.optionalForm,
        enableImages: true,
        imagesTextToImageModelId: '',
        imagesImageToImageModelId: 'black-forest-labs/FLUX.2-dev',
        imagesTimeoutSeconds: '30',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.imagesTextToImageModelId).toContain('Text-to-image');
  });

  it('validates speech transcription fields when enabled', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setOptionalForm({
        ...result.current.optionalForm,
        enableSpeechTranscription: true,
        speechTranscriptionModelId: '   ',
        speechTranscriptionTimeoutSeconds: '30',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.speechTranscriptionModelId).toContain('Transcription model id');
  });

  it('validates speech synthesis timeout when enabled', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useHuggingFaceWizardState());

    act(() => {
      result.current.setOptionalForm({
        ...result.current.optionalForm,
        enableSpeechSynthesis: true,
        speechSynthesisModelId: 'facebook/mms-tts-eng',
        speechSynthesisTimeoutSeconds: '0',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.speechSynthesisTimeoutSeconds).toContain('positive integer');
  });
});
