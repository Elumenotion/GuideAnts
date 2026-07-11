import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { api } from '../../../../services/api';
import { GEMINI_CORE_SECTION, GEMINI_SERVICE_PROVIDER_IDS, WIZARD_DEFER_WARMUP_OPTIONS } from '../constants';
import { useGeminiWizardState } from '../useGeminiWizardState';
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

describe('useGeminiWizardState', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getSection).mockImplementation(async (sectionName: string) =>
      createSection(sectionName, sectionName === GEMINI_CORE_SECTION ? { ApiKey: '' } : {})
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
      defaultModelId: 'gemini-2.5-flash',
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
        [GEMINI_CORE_SECTION]: createSection(GEMINI_CORE_SECTION, { ApiKey: '' }),
      },
    });
    const { result } = renderHook(() => useGeminiWizardState());

    act(() => {
      result.current.setCoreForm({ apiKey: 'short' });
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
    const { result } = renderHook(() => useGeminiWizardState());

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.apiKey).toBe('API key is required.');
  });

  it('persists connection details and refreshes snapshot', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [GEMINI_CORE_SECTION]: createSection(GEMINI_CORE_SECTION, { ApiKey: '' }),
      },
    });
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useGeminiWizardState());

    act(() => {
      result.current.setCoreForm({ apiKey: 'gemini-secret-key-12345' });
    });

    await act(async () => {
      await result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), setSnapshot);
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      GEMINI_CORE_SECTION,
      expect.objectContaining({
        payload: expect.objectContaining({ ApiKey: 'gemini-secret-key-12345' }),
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
          modelId: 'gemini-2.5-flash',
          displayName: 'gemini-2.5-flash',
          provider: 'google-gemini-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const loadSnapshot = createLoadSnapshot(refreshed);
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useGeminiWizardState());

    act(() => {
      result.current.setDraftModelId('gemini-2.5-flash');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.draftModels).toHaveLength(1);

    await act(async () => {
      await result.current.persistModels(snapshot, loadSnapshot, setSnapshot);
    });

    expect(api.settings.addModel).toHaveBeenCalled();
    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
      expect.objectContaining({ defaultModelId: 'gemini-2.5-flash' })
    );
    expect(result.current.draftModels).toHaveLength(0);
  });

  it('validates optional speech synthesis fields before persistence', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useGeminiWizardState());

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

  it('rejects persisting models when none are configured', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useGeminiWizardState());

    await act(async () => {
      await expect(
        result.current.persistModels(snapshot, createLoadSnapshot(snapshot), createSetSnapshot())
      ).rejects.toThrow('Model requirement not met.');
    });
    expect(result.current.modelStepError).toContain('At least one Gemini model is required');
  });

  it('rejects duplicate queued model ids', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useGeminiWizardState());

    act(() => {
      result.current.setDraftModelId('gemini-custom');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });
    act(() => {
      result.current.setDraftModelId('gemini-custom');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    expect(result.current.modelAddError).toContain('already queued');
  });

  it('persists enabled optional services', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useGeminiWizardState());

    await act(async () => {
      await result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'SpeechTranscription',
      GEMINI_SERVICE_PROVIDER_IDS.SpeechTranscription,
      expect.objectContaining({ ModelId: expect.any(String) })
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'Embeddings',
      GEMINI_SERVICE_PROVIDER_IDS.Embeddings,
      WIZARD_DEFER_WARMUP_OPTIONS
    );
  });
});
