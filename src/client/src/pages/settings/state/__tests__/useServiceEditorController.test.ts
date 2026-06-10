import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { ProviderEditorStateDto, ServiceEditorStateDto } from '../../../../types/settings';
import { useServiceEditorController } from '../useServiceEditorController';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      services: {
        get: vi.fn(),
        updateProviderFields: vi.fn(),
        updateActiveProvider: vi.fn(),
      },
    },
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
import { api } from '../../../../services/api';

function makeProvider(overrides: Partial<ProviderEditorStateDto> = {}): ProviderEditorStateDto {
  return {
    providerId: 'SpeechSynthesis.Local.Tts',
    providerKind: 'Local',
    providerSection: 'LocalTts',
    hasExplicitMode: true,
    isDefaultMode: true,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate: true,
    activationBlockers: [],
    fields: {
      Endpoint: { name: 'Endpoint', value: 'http://localhost:8110', isSecret: false, hasValue: true },
    },
    runtimeDependencies: [],
    operativeFields: ['Endpoint'],
    diagnosticFields: [],
    fieldMetadata: [
      {
        name: 'Endpoint',
        kind: 'url',
        required: true,
        enumOptions: null,
        operative: true,
      },
    ],
    ...overrides,
  };
}

function makeServiceState(overrides: Partial<ServiceEditorStateDto> = {}): ServiceEditorStateDto {
  const provider = makeProvider();
  return {
    serviceId: 'SpeechSynthesis',
    activeProviderId: provider.providerId,
    providers: [provider],
    readiness: { status: 'ready', blockers: [], warnings: [] },
    ...overrides,
  };
}

describe('useServiceEditorController', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  it('loads service editor state on mount', async () => {
    const state = makeServiceState();
    (api.settings.services.get as any).mockResolvedValue(state);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    expect(api.settings.services.get).toHaveBeenCalledWith('SpeechSynthesis');
    expect(result.current.state).toEqual(state);
    expect(result.current.error).toBeNull();
    expect(result.current.selectedProvider?.providerId).toBe('SpeechSynthesis.Local.Tts');
    expect(result.current.providerOptions).toHaveLength(1);
  });

  it('surfaces load failures', async () => {
    (api.settings.services.get as any).mockRejectedValue(new Error('network down'));

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    expect(result.current.state).toBeNull();
    expect(result.current.error).toBe('network down');
  });

  it('blocks save when provider connection is not configured', async () => {
    const disconnected = makeProvider({
      connectionConfigured: false,
      connectionMissingFields: ['ApiKey'],
    });
    const state = makeServiceState({ providers: [disconnected], activeProviderId: disconnected.providerId });
    (api.settings.services.get as any).mockResolvedValue(state);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(false);
    expect(result.current.error).toMatch(/Configure the provider connection/i);
    expect(api.settings.services.updateProviderFields).not.toHaveBeenCalled();
    expect(api.settings.services.updateActiveProvider).not.toHaveBeenCalled();
  });

  it('blocks save when operative field validation fails', async () => {
    const provider = makeProvider({
      fields: {
        Endpoint: { name: 'Endpoint', value: '', isSecret: false, hasValue: false },
      },
    });
    const state = makeServiceState({ providers: [provider], activeProviderId: provider.providerId });
    (api.settings.services.get as any).mockResolvedValue(state);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(false);
    expect(result.current.error).toBe('Fix the highlighted fields before saving.');
    expect(result.current.fieldErrors.Endpoint).toBeDefined();
    expect(api.settings.services.updateProviderFields).not.toHaveBeenCalled();
  });

  it('saves provider fields and active provider, then reloads', async () => {
    const initial = makeServiceState();
    const reloaded = makeServiceState({
      readiness: { status: 'ready', blockers: [], warnings: ['saved'] },
    });
    (api.settings.services.get as any)
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(reloaded);
    (api.settings.services.updateProviderFields as any).mockResolvedValue(undefined);
    (api.settings.services.updateActiveProvider as any).mockResolvedValue(undefined);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(true);
    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.Local.Tts',
      { Endpoint: 'http://localhost:8110' }
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.Local.Tts'
    );
    expect(api.settings.services.get).toHaveBeenCalledTimes(2);
    expect(result.current.state?.readiness.warnings).toEqual(['saved']);
  });

  it('hides unconfigured cloud providers from providerOptions', async () => {
    const local = makeProvider({ providerId: 'SpeechSynthesis.Local.Tts', providerKind: 'Local' });
    const hiddenCloud = makeProvider({
      providerId: 'SpeechSynthesis.AzureSpeechService.Cloud',
      providerKind: 'Cloud',
      connectionConfigured: false,
      connectionMissingFields: ['SubscriptionKey'],
      canActivate: false,
      activationBlockers: ['Connection not configured'],
    });
    const state = makeServiceState({
      providers: [local, hiddenCloud],
      activeProviderId: local.providerId,
    });
    (api.settings.services.get as any).mockResolvedValue(state);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    expect(result.current.providerOptions).toHaveLength(1);
    expect(result.current.providerOptions[0].providerId).toBe('SpeechSynthesis.Local.Tts');
  });

  it('clearFieldError removes a field error entry', async () => {
    const provider = makeProvider({
      fields: {
        Endpoint: { name: 'Endpoint', value: '', isSecret: false, hasValue: false },
      },
    });
    const state = makeServiceState({ providers: [provider], activeProviderId: provider.providerId });
    (api.settings.services.get as any).mockResolvedValue(state);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    await act(async () => {
      await result.current.save();
    });
    expect(result.current.fieldErrors.Endpoint).toBeDefined();

    act(() => {
      result.current.clearFieldError('Endpoint');
    });

    expect(result.current.fieldErrors.Endpoint).toBeUndefined();
  });
});
