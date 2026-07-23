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

  it('allows save when Foundry connection fields are editable inline', async () => {
    const foundry = makeProvider({
      providerId: 'SpeechTranscription.AzureSpeech.Batch',
      providerKind: 'Cloud',
      connectionConfigured: false,
      connectionMissingFields: ['Endpoint', 'ApiKey'],
      relatedChatConnectionConfigured: true,
      operativeFields: ['Endpoint', 'ApiKey', 'Region', 'TimeoutSeconds'],
      fields: {
        Endpoint: { name: 'Endpoint', value: '', isSecret: false, hasValue: false },
        ApiKey: { name: 'ApiKey', value: '', isSecret: true, hasValue: false },
        Region: { name: 'Region', value: '', isSecret: false, hasValue: false },
        TimeoutSeconds: { name: 'TimeoutSeconds', value: '300', isSecret: false, hasValue: true },
      },
      fieldMetadata: [
        { name: 'Endpoint', kind: 'url', required: true, enumOptions: null, operative: true },
        { name: 'ApiKey', kind: 'secret', required: true, enumOptions: null, operative: true },
        { name: 'Region', kind: 'text', required: true, enumOptions: null, operative: true },
        { name: 'TimeoutSeconds', kind: 'int', required: true, enumOptions: null, operative: true },
      ],
    });
    const state = makeServiceState({ providers: [foundry], activeProviderId: foundry.providerId });
    (api.settings.services.get as any).mockResolvedValue(state);
    (api.settings.services.updateProviderFields as any).mockResolvedValue(undefined);
    (api.settings.services.updateActiveProvider as any).mockResolvedValue(undefined);

    const { result } = renderHook(() => useServiceEditorController('SpeechTranscription'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    act(() => {
      result.current.draft.patchActiveDraft({
        Endpoint: 'https://speech.example.com/',
        ApiKey: 'speech-key',
        Region: 'eastus',
      });
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(true);
    expect(api.settings.services.updateProviderFields).toHaveBeenCalled();
  });

  it('blocks save when provider connection is not configured', async () => {
    const disconnected = makeProvider({
      connectionConfigured: false,
      connectionMissingFields: ['ApiKey'],
      operativeFields: ['TimeoutSeconds'],
      fields: {
        TimeoutSeconds: { name: 'TimeoutSeconds', value: '30', isSecret: false, hasValue: true },
      },
      fieldMetadata: [
        { name: 'TimeoutSeconds', kind: 'int', required: true, enumOptions: null, operative: true },
      ],
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

    act(() => {
      result.current.draft.setDraftForProvider('SpeechSynthesis.Local.Tts', {
        Endpoint: 'http://localhost:8120',
      });
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(true);
    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.Local.Tts',
      { Endpoint: 'http://localhost:8120' }
    );
    expect(api.settings.services.updateActiveProvider).not.toHaveBeenCalled();
    expect(api.settings.services.get).toHaveBeenCalledTimes(2);
    expect(result.current.state?.readiness.warnings).toEqual(['saved']);
  });

  it('skips save API calls when provider and fields are unchanged', async () => {
    const initial = makeServiceState();
    (api.settings.services.get as any).mockResolvedValue(initial);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(true);
    expect(api.settings.services.updateProviderFields).not.toHaveBeenCalled();
    expect(api.settings.services.updateActiveProvider).not.toHaveBeenCalled();
    expect(api.settings.services.get).toHaveBeenCalledTimes(1);
  });

  it('updates active provider without field writes when only provider changes', async () => {
    const local = makeProvider({ providerId: 'SpeechSynthesis.Local.Tts' });
    const cloud = makeProvider({
      providerId: 'SpeechSynthesis.AzureSpeechService.Cloud',
      providerKind: 'Cloud',
      providerSection: 'AzureSpeech',
      connectionConfigured: true,
    });
    const initial = makeServiceState({
      providers: [local, cloud],
      activeProviderId: local.providerId,
    });
    const reloaded = makeServiceState({
      providers: [local, cloud],
      activeProviderId: cloud.providerId,
    });
    (api.settings.services.get as any)
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(reloaded);
    (api.settings.services.updateActiveProvider as any).mockResolvedValue(undefined);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    act(() => {
      result.current.draft.switchProvider(cloud.providerId);
    });

    let saved = false;
    await act(async () => {
      saved = await result.current.save();
    });

    expect(saved).toBe(true);
    expect(api.settings.services.updateProviderFields).not.toHaveBeenCalled();
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.AzureSpeechService.Cloud',
      { deferWarmup: false }
    );
  });

  it('hides unconfigured cloud providers from providerOptions', async () => {
    const local = makeProvider({ providerId: 'SpeechSynthesis.Local.Tts', providerKind: 'Local' });
    const hiddenCloud = makeProvider({
      providerId: 'SpeechSynthesis.OpenAI.Tts',
      providerKind: 'Cloud',
      connectionConfigured: false,
      connectionMissingFields: ['ApiKey'],
      canActivate: false,
      activationBlockers: ['Connection not configured'],
      operativeFields: ['TimeoutSeconds', 'VoiceName'],
      relatedChatConnectionConfigured: false,
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

  it('keeps Foundry cloud providers visible when related chat connection is configured', async () => {
    const local = makeProvider({ providerId: 'SpeechSynthesis.Local.Tts', providerKind: 'LocalHttp' });
    const foundry = makeProvider({
      providerId: 'SpeechSynthesis.AzureSpeech.Ssml',
      providerKind: 'Cloud',
      providerSection: 'AzureSpeechService',
      connectionConfigured: false,
      connectionMissingFields: ['ApiKey', 'Region'],
      relatedChatConnectionConfigured: true,
      canActivate: false,
      activationBlockers: ['Missing provider connection value: ApiKey.'],
    });
    const state = makeServiceState({
      providers: [local, foundry],
      activeProviderId: local.providerId,
    });
    (api.settings.services.get as any).mockResolvedValue(state);

    const { result } = renderHook(() => useServiceEditorController('SpeechSynthesis'));

    await waitFor(() => {
      expect(result.current.loading).toBe(false);
    });

    expect(result.current.providerOptions.map((p) => p.providerId)).toEqual([
      'SpeechSynthesis.Local.Tts',
      'SpeechSynthesis.AzureSpeech.Ssml',
    ]);
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
