import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { api } from '../../../../services/api';
import { OPENROUTER_SECTION, OPENROUTER_SERVICE_PROVIDER_IDS } from '../constants';
import { useOpenRouterWizardState } from '../useOpenRouterWizardState';
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

describe('useOpenRouterWizardState', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getSection).mockImplementation(async (sectionName: string) =>
      createSection(
        sectionName,
        sectionName === OPENROUTER_SECTION
          ? { ApiKey: '', BaseUrl: 'https://openrouter.ai/api/v1' }
          : {}
      )
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
      defaultModelId: 'minimax/minimax-m3',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });
    vi.mocked(api.settings.services.updateProviderFields).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.services.updateActiveProvider).mockResolvedValue(undefined as never);
  });

  it('rejects connection persistence when api key is missing', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.apiKey).toBe('API key is required.');
  });

  it('rejects adding duplicate draft models', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    act(() => {
      result.current.setDraftModelId('minimax/minimax-m3');
      result.current.addDraftModel(snapshot, 0, 0);
    });
    act(() => {
      result.current.setDraftModelId('minimax/minimax-m3');
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.modelAddError).toContain('already queued');
  });

  it('rejects connection persistence when base url is empty', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    act(() => {
      result.current.setCoreForm({
        apiKey: 'openrouter-secret-key-12345',
        baseUrl: '   ',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.baseUrl).toBe('Base URL is required.');
  });

  it('persists connection details', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    act(() => {
      result.current.setCoreForm({
        apiKey: 'openrouter-secret-key-12345',
        baseUrl: 'https://openrouter.ai/api/v1',
      });
    });

    await act(async () => {
      await result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      OPENROUTER_SECTION,
      expect.objectContaining({
        payload: expect.objectContaining({
          ApiKey: 'openrouter-secret-key-12345',
          BaseUrl: 'https://openrouter.ai/api/v1',
        }),
      })
    );
  });

  it('queues and persists openrouter chat models', async () => {
    const snapshot = createWizardSnapshot();
    const refreshed = createWizardSnapshot({
      models: [
        {
          modelId: 'minimax/minimax-m3',
          displayName: 'minimax/minimax-m3',
          provider: 'openrouter-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const { result } = renderHook(() => useOpenRouterWizardState());

    act(() => {
      result.current.setDraftModelId('minimax/minimax-m3');
      result.current.addDraftModel(snapshot, 0, 0);
    });

    await act(async () => {
      await result.current.persistModels(snapshot, createLoadSnapshot(refreshed), createSetSnapshot());
    });

    expect(api.settings.addModel).toHaveBeenCalledWith(
      expect.objectContaining({
        provider: 'openrouter-chat',
        catalog: expect.objectContaining({ modelId: 'minimax/minimax-m3' }),
      })
    );
  });

  it('validates optional embeddings timeout before persistence', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    act(() => {
      result.current.setOptionalForm({ embeddingsTimeoutSeconds: '0' });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.embeddingsTimeoutSeconds).toBe('Timeout must be a positive integer.');
  });

  it('rejects persisting models when none are configured', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    await act(async () => {
      await expect(
        result.current.persistModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Model requirement not met.');
    });
    expect(result.current.modelStepError).toContain('At least one OpenRouter model is required');
  });

  it('rejects persisting models that already exist in the catalog', async () => {
    const snapshot = createWizardSnapshot({
      models: [
        {
          modelId: 'minimax/minimax-m3',
          displayName: 'minimax/minimax-m3',
          provider: 'openrouter-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    vi.mocked(api.settings.getModels).mockResolvedValue(snapshot.models as never);
    const { result } = renderHook(() => useOpenRouterWizardState());

    act(() => {
      result.current.setDraftModelId('minimax/minimax-m3');
      result.current.addDraftModel(createWizardSnapshot(), 1, 0);
    });

    await act(async () => {
      await expect(
        result.current.persistModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Model id conflict detected.');
    });
    expect(result.current.modelStepError).toContain('already exists');
  });

  it('persists enabled optional services', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useOpenRouterWizardState());

    await act(async () => {
      await result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'Embeddings',
      OPENROUTER_SERVICE_PROVIDER_IDS.Embeddings,
      expect.objectContaining({ ModelId: 'nvidia/llama-nemotron-embed-vl-1b-v2:free' })
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechTranscription',
      OPENROUTER_SERVICE_PROVIDER_IDS.SpeechTranscription
    );
  });
});
