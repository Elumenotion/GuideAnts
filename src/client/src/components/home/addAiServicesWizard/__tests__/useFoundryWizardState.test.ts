import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { api } from '../../../../services/api';
import {
  FOUNDRY_CORE_SECTION,
  FOUNDRY_SERVICE_PROVIDER_IDS,
  SECRET_MASK,
  WIZARD_DEFER_WARMUP_OPTIONS,
} from '../constants';
import { useFoundryWizardState } from '../useFoundryWizardState';
import {
  createLoadSnapshot,
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

describe('useFoundryWizardState', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getSections).mockResolvedValue([]);
    vi.mocked(api.settings.getModels).mockResolvedValue([]);
    vi.mocked(api.settings.getSection).mockImplementation(async (sectionName: string) =>
      createSection(
        sectionName,
        sectionName === FOUNDRY_CORE_SECTION
          ? { Resource: 'my-resource', ApiKey: '', ApiVersion: '2025-04-01-preview' }
          : {},
      ),
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
      defaultModelId: 'gpt-4o',
      overrideAllChatModels: false,
      temperature: null,
      topP: null,
      reasoningEffort: null,
      samplingParametersJson: null,
    });
    vi.mocked(api.settings.services.updateProviderFields).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.services.updateActiveProvider).mockResolvedValue(undefined as never);
  });

  it('derives core endpoint from resource and syncs linked optional endpoints', async () => {
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({ resource: 'my-foundry-resource' });
    });

    expect(result.current.derivedCoreEndpoint).toBe('https://my-foundry-resource.openai.azure.com/');
    await waitFor(() => {
      expect(result.current.optionalForm.embeddingsEndpoint).toBe(
        'https://my-foundry-resource.openai.azure.com/',
      );
    });
  });

  it('rejects connection persistence when required fields are missing', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot()),
      ).rejects.toThrow('Connection details are incomplete.');
    });

    expect(result.current.coreErrors.resource).toBe('Resource is required.');
    expect(result.current.coreErrors.apiKey).toBe('API key is required.');
  });

  it('rejects short api keys during connection validation', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({
        resource: 'my-resource',
        apiKey: 'short',
        apiVersion: '2025-04-01-preview',
      });
    });

    await act(async () => {
      await expect(
        result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), createSetSnapshot()),
      ).rejects.toThrow('Connection details are incomplete.');
    });
    expect(result.current.coreErrors.apiKey).toBe('API key looks too short.');
  });

  it('persists connection details and refreshes snapshot', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [FOUNDRY_CORE_SECTION]: createSection(FOUNDRY_CORE_SECTION, {
          Resource: 'my-resource',
          ApiKey: '',
          ApiVersion: '',
        }),
      },
    });
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({
        resource: 'my-resource',
        apiKey: 'foundry-secret-key-12345',
        apiVersion: '2025-04-01-preview',
      });
    });

    await act(async () => {
      await result.current.persistConnection(snapshot, createLoadSnapshot(snapshot), setSnapshot);
    });

    expect(api.settings.updateSection).toHaveBeenCalledWith(
      FOUNDRY_CORE_SECTION,
      expect.objectContaining({
        payload: expect.objectContaining({
          Resource: 'my-resource',
          ApiKey: 'foundry-secret-key-12345',
        }),
      }),
    );
    expect(setSnapshot).toHaveBeenCalled();
    expect(result.current.coreForm.apiKeyHasStoredValue).toBe(true);
  });

  it('queues draft models and rejects duplicates', () => {
    const snapshot = createWizardSnapshot({
      models: [
        {
          modelId: 'gpt-4o',
          displayName: 'gpt-4o',
          provider: 'azure-openai-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setDraftModelId('gpt-4o');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 1, 0);
    });
    expect(result.current.modelAddError).toContain('already exists');

    act(() => {
      result.current.setDraftModelId('gpt-4o-mini');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 1, 0);
      result.current.setDraftModelId('gpt-4o-mini');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 1, 0);
    });
    expect(result.current.modelAddError).toContain('already queued');
  });

  it('persists queued models and sets global default', async () => {
    const snapshot = createWizardSnapshot();
    const refreshed = createWizardSnapshot({
      models: [
        {
          modelId: 'gpt-4o-mini',
          displayName: 'gpt-4o-mini',
          provider: 'azure-openai-chat',
          isActive: true,
          created: '2026-04-29T00:00:00Z',
        },
      ],
    });
    const loadSnapshot = createLoadSnapshot(refreshed);
    const setSnapshot = createSetSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setDraftModelId('gpt-4o-mini');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    await act(async () => {
      await result.current.persistModels(snapshot, loadSnapshot, setSnapshot);
    });

    expect(api.settings.addModel).toHaveBeenCalled();
    expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
      expect.objectContaining({ defaultModelId: 'gpt-4o-mini' }),
    );
    expect(result.current.draftModels).toHaveLength(0);
  });

  it('reports empty draft model id as validation error', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });
    expect(result.current.modelAddError).toBe('Model is required.');
  });

  it('fails hard when the first model cannot become the global default', async () => {
    const snapshot = createWizardSnapshot();
    const refreshed = createWizardSnapshot({
      models: [{ modelId: 'gpt-4o-mini', displayName: 'gpt-4o-mini', provider: 'azure-openai-chat', isActive: true, created: '2026-04-29T00:00:00Z' }],
    });
    vi.mocked(api.settings.chatDefaults.update).mockRejectedValueOnce(new Error('Default update failed.'));
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setDraftModelId('gpt-4o-mini');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });

    await act(async () => {
      await expect(
        result.current.persistModels(snapshot, createLoadSnapshot(refreshed), createSetSnapshot())
      ).rejects.toThrow('Default update failed.');
    });
  });

  it('surfaces global default warning without failing when additional models are added', async () => {
    const snapshot = createWizardSnapshot({
      models: [{ modelId: 'gpt-4o', displayName: 'gpt-4o', provider: 'azure-openai-chat', isActive: true, created: '2026-04-29T00:00:00Z' }],
    });
    const refreshed = createWizardSnapshot({
      models: [
        { modelId: 'gpt-4o', displayName: 'gpt-4o', provider: 'azure-openai-chat', isActive: true, created: '2026-04-29T00:00:00Z' },
        { modelId: 'gpt-4o-mini', displayName: 'gpt-4o-mini', provider: 'azure-openai-chat', isActive: true, created: '2026-04-29T00:00:00Z' },
      ],
    });
    const onWarning = vi.fn();
    vi.mocked(api.settings.chatDefaults.update).mockRejectedValueOnce(new Error('Default update failed.'));
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setDraftModelId('gpt-4o-mini');
      result.current.setDraftAsGlobalDefault(true);
    });
    act(() => {
      result.current.addDraftModel(snapshot, 1, 0);
    });

    await act(async () => {
      await result.current.persistModels(snapshot, createLoadSnapshot(refreshed), createSetSnapshot(), onWarning);
    });

    expect(onWarning).toHaveBeenCalledWith(expect.stringContaining('global default failed'));
    expect(result.current.draftModels).toHaveLength(0);
  });

  it('persists optional image generation configuration', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [FOUNDRY_CORE_SECTION]: createSection(FOUNDRY_CORE_SECTION, { Resource: 'my-resource' }),
      },
    });
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({ resource: 'my-resource' });
      result.current.setOptionalForm({
        enableImages: true,
        linkImagesEndpointToCore: false,
        imagesEndpoint: 'https://images.example.com/',
        imagesApiKey: 'images-key-12345678',
        imagesApiVersion: '2024-10-01',
        imagesDeployment: 'dalle-3',
        imagesEditDeployment: 'dalle-3-edit',
      });
    });

    await act(async () => {
      await result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot());
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'ImageGeneration',
      FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration,
      expect.objectContaining({
        Deployment: 'dalle-3',
        EditModelDeployment: 'dalle-3-edit',
      }),
    );
  });

  it('validates optional services before persistence', async () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({ resource: 'my-resource' });
      result.current.setOptionalForm({ enableEmbeddings: true });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot()),
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });

    expect(result.current.optionalErrors.embeddingsApiKey).toBe('API key is required.');
    expect(result.current.optionalErrors.embeddingsDeployment).toBe('Deployment is required.');
  });

  it('persists enabled optional services and masks stored secrets', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [FOUNDRY_CORE_SECTION]: createSection(FOUNDRY_CORE_SECTION, {
          Resource: 'my-resource',
          ApiVersion: '2025-04-01-preview',
        }),
      },
    });
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({ resource: 'my-resource' });
      result.current.setOptionalForm({
        enableEmbeddings: true,
        embeddingsApiKey: 'embed-key-12345678',
        embeddingsDeployment: 'text-embedding-3-small',
        enableSpeech: true,
        speechEndpoint: 'https://speech.example.com/',
        speechApiKey: 'speech-key-12345678',
        speechRegion: 'westus2',
        enableDocumentIntelligence: true,
        documentIntelligenceEndpoint: 'https://doc.example.com/',
        documentIntelligenceApiKey: 'doc-key-12345678',
      });
    });

    await act(async () => {
      await result.current.persistOptionalServices(
        snapshot,
        createLoadSnapshot(snapshot),
        createSetSnapshot(),
      );
    });

    expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
      'Embeddings',
      FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings,
      expect.objectContaining({ Deployment: 'text-embedding-3-small' }),
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'SpeechTranscription',
      FOUNDRY_SERVICE_PROVIDER_IDS.SpeechTranscription,
      WIZARD_DEFER_WARMUP_OPTIONS
    );
    expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
      'DocumentIntelligence',
      FOUNDRY_SERVICE_PROVIDER_IDS.DocumentIntelligence,
      WIZARD_DEFER_WARMUP_OPTIONS
    );
    expect(result.current.optionalForm.embeddingsApiKey).toBe(SECRET_MASK);
    expect(result.current.optionalForm.speechApiKeyHasStoredValue).toBe(true);
  });

  it('validates optional speech fields before persistence', async () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [FOUNDRY_CORE_SECTION]: createSection(FOUNDRY_CORE_SECTION, { Resource: 'my-resource' }),
      },
    });
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setCoreForm({ resource: 'my-resource' });
      result.current.setOptionalForm({ enableSpeech: true, speechRegion: '' });
    });

    await act(async () => {
      await expect(
        result.current.persistOptionalServices(snapshot, createLoadSnapshot(snapshot), createSetSnapshot()),
      ).rejects.toThrow('Optional service inputs are incomplete.');
    });
    expect(result.current.optionalErrors.speechRegion).toBe('Region is required.');
  });

  it('resets state from snapshot', () => {
    const snapshot = createWizardSnapshot({
      sectionsByName: {
        ...createWizardSnapshot().sectionsByName,
        [FOUNDRY_CORE_SECTION]: createSection(FOUNDRY_CORE_SECTION, {
          Resource: 'stored-resource',
          ApiVersion: '2025-04-01-preview',
        }, { ApiKey: true }),
      },
      serviceStates: {
        Embeddings: createServiceState('Embeddings', FOUNDRY_SERVICE_PROVIDER_IDS.Embeddings),
        ImageGeneration: createServiceState('ImageGeneration', FOUNDRY_SERVICE_PROVIDER_IDS.ImageGeneration),
        SpeechTranscription: createServiceState('SpeechTranscription', FOUNDRY_SERVICE_PROVIDER_IDS.SpeechTranscription),
        SpeechSynthesis: createServiceState('SpeechSynthesis', FOUNDRY_SERVICE_PROVIDER_IDS.SpeechSynthesis),
        DocumentIntelligence: createServiceState('DocumentIntelligence', FOUNDRY_SERVICE_PROVIDER_IDS.DocumentIntelligence),
      },
    });
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setDraftModelId('temp-model');
      result.current.setCoreForm({ resource: 'temp' });
      result.current.resetWithSnapshot(snapshot);
    });

    expect(result.current.coreForm.resource).toBe('stored-resource');
    expect(result.current.draftModels).toHaveLength(0);
    expect(result.current.draftModelId).toBe('');
    expect(result.current.modelAddError).toBeNull();
  });

  it('removes queued draft models', () => {
    const snapshot = createWizardSnapshot();
    const { result } = renderHook(() => useFoundryWizardState());

    act(() => {
      result.current.setDraftModelId('gpt-4o');
    });
    act(() => {
      result.current.addDraftModel(snapshot, 0, 0);
    });
    const localId = result.current.draftModels[0]?.localId;
    expect(localId).toBeTruthy();

    act(() => {
      result.current.removeDraftModel(localId!);
    });
    expect(result.current.draftModels).toHaveLength(0);
  });
});
