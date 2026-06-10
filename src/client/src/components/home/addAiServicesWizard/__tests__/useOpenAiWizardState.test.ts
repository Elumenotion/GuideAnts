import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { api } from '../../../../services/api';
import { OPENAI_CORE_SECTION, OPENAI_SERVICE_PROVIDER_IDS } from '../constants';
import { useOpenAiWizardState } from '../useOpenAiWizardState';
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

describe('useOpenAiWizardState', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getSection).mockImplementation(async (sectionName: string) =>
      createSection(sectionName, sectionName === OPENAI_CORE_SECTION ? { ApiKey: '', Endpoint: '' } : {})
    );
    vi.mocked(api.settings.updateSection).mockImplementation(async (sectionName, request) => ({
      ...createSection(sectionName),
      rowVersion: '2',
      payload: request.payload,
      secretHasValue: { ApiKey: true },
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
      defaultModelId: 'gpt-4.1-mini',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });
    vi.mocked(api.settings.services.updateProviderFields).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.services.updateActiveProvider).mockResolvedValue(undefined as never);
  });

  it('rejects api keys that look too short', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [OPENAI_CORE_SECTION]: createSection(OPENAI_CORE_SECTION, { ApiKey: '', Endpoint: '' }),
      },
    });
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setCoreForm({ apiKey: 'tiny' });
    });

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.apiKey).toBe('API key looks too short.');
  });

  it('rejects connection persistence when api key is missing', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.apiKey).toBe('API key is required.');
  });

  it('persists connection details and refreshes snapshot', async () => {
    const snapshot = createWizardSnapshot();
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setCoreForm({ apiKey: 'openai-secret-key-12345' });
    });

    await act(async () => {
      await result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), setSnapshot);
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      OPENAI_CORE_SECTION,
      expect.objectContaining({
        payload: expect.objectContaining({ ApiKey: 'openai-secret-key-12345' }),
      })
    );
    expect(setSnapshot).toHaveBeenCalled();
    expect(result.current.coreForm.apiKeyHasStoredValue).toBe(true);
  });

  it('queues draft models and persists them with global default', async () => {
    const snapshot = createWizardSnapshot();
    const refreshed = createWizardSnapshot({
      models: [
        {
          modelId: 'gpt-4.1-mini',
          displayName: 'gpt-4.1-mini',
          provider: 'openai-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const loadSnapshot = createLoadSnapshot(refreshed);
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setDraftModelId('gpt-4.1-mini');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.draftModels).toHaveLength(1);
    expect(result.current.draftModels[0]?.setAsGlobalDefault).toBe(true);

    await act(async () => {
      await result.current.persistModels(snapshot, loadSnapshot, setSnapshot);
    });

    expect(api.settings.addModel).toHaveBeenCalledWith(
      expect.objectContaining({
        provider: 'openai-chat',
        catalog: expect.objectContaining({ modelId: 'gpt-4.1-mini' }),
      })
    );
    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
      expect.objectContaining({ defaultModelId: 'gpt-4.1-mini' })
    );
    expect(result.current.draftModels).toHaveLength(0);
  });

  it('validates optional speech synthesis fields before persistence', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setOptionalForm({ speechSynthesisVoiceName: '' });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.speechSynthesisVoiceName).toBe('Voice name is required.');
    expect(api.settings.services.updateProviderFields).not.toHaveBeenCalled();
  });

  it('persists enabled optional services', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    await act(async () => {
      await result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'SpeechTranscription',
      OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription,
      expect.objectContaining({ ModelId: 'whisper-1' })
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'Embeddings',
      OPENAI_SERVICE_PROVIDER_IDS.Embeddings
    );
  });

  it('persists optional endpoint when provided', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [OPENAI_CORE_SECTION]: createSection(OPENAI_CORE_SECTION, { ApiKey: '', Endpoint: '' }),
      },
    });
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setCoreForm({
        apiKey: 'openai-secret-key-12345',
        endpoint: 'https://custom.openai.example/v1',
      });
    });

    await act(async () => {
      await result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), setSnapshot);
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      OPENAI_CORE_SECTION,
      expect.objectContaining({
        payload: expect.objectContaining({
          Endpoint: 'https://custom.openai.example/v1',
        }),
      })
    );
  });

  it('rejects duplicate queued model ids before persistence', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setDraftModelId('first-custom-model');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });
    act(() => {
      result.current.setDraftModelId('first-custom-model');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.modelAddError).toContain('already queued');
  });

  it('rejects persistence when model id already exists in catalog', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenAiWizardState());

    act(() => {
      result.current.setDraftModelId('brand-new-model');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    vi.mocked(api.settings.getModels).mockResolvedValueOnce([
      {
        modelId: 'brand-new-model',
        displayName: 'Brand New',
        provider: 'openai-chat',
        isActive: true,
        created: '2026-04-29T00:00:00Z',
      },
    ]);

    await act(async () => {
      await expect(
        result.current.persistModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Model id conflict');
    });
    expect(result.current.modelStepError).toContain('already exists');
  });
});
