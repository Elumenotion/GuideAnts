import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import AddAiServicesWizard from '../AddAiServicesWizard';
import { api } from '../../../services/api';
import {
  HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS,
  HUGGINGFACE_SERVICE_PROVIDER_IDS,
  OPENAI_SERVICE_PROVIDER_IDS,
  OPENROUTER_SERVICE_PROVIDER_IDS,
} from '../addAiServicesWizard/constants';

vi.mock('../../../services/api', () => ({
  api: {
    settings: {
      getSections: vi.fn(),
      getSchema: vi.fn(),
      getModels: vi.fn(),
      getSection: vi.fn(),
      updateSection: vi.fn(),
      addModel: vi.fn(),
      getRuntimeProfiles: vi.fn(),
      getLlamaInventory: vi.fn(),
      chatDefaults: {
        get: vi.fn(),
        update: vi.fn(),
      },
      services: {
        get: vi.fn(),
        updateProviderFields: vi.fn(),
        updateActiveProvider: vi.fn(),
      },
      localModels: {
        cancelOperation: vi.fn(),
        getOperation: vi.fn(),
      },
    },
  },
}));

const NOW = '2026-04-29T00:00:00Z';

describe('AddAiServicesWizard', () => {
  beforeEach(() => {
    vi.clearAllMocks();

    let rowVersion = 1;
    let coreResource = '';
    let coreApiVersion = '2025-04-01-preview';
    let coreApiKeyStored = false;
    let geminiApiKeyStored = false;
    let openAiApiKeyStored = false;
    let openAiEndpoint = '';
    let hfTokenStored = false;
    let hfRouterBaseUrl = HUGGINGFACE_OPTIONAL_SERVICE_DEFAULTS.routerBaseUrl;
    let openRouterApiKeyStored = false;
    let openRouterBaseUrl = 'https://openrouter.ai/api/v1';
    let models: Array<{
      modelId: string;
      displayName: string;
      provider: string;
      isActive: boolean;
      created: string;
    }> = [];
    let chatDefaults = {
      rowVersion: '1',
      defaultModelId: null as string | null,
      overrideAllChatModels: false,
      temperature: null as number | null,
      topP: null as number | null,
      reasoningEffort: null as string | null,
      samplingParametersJson: null as string | null,
    };

    const getSectionSummaries = () => ([
      {
        sectionName: 'AzureOpenAI',
        hasSecrets: true,
        readinessStatus: coreResource.trim().length > 0 && coreApiKeyStored ? 'configured' : 'unconfigured',
        missingFields: coreResource.trim().length > 0 && coreApiKeyStored ? [] : ['Resource', 'ApiKey'],
      },
      {
        sectionName: 'AzureOpenAiEmbedding',
        hasSecrets: true,
        readinessStatus: 'unconfigured',
        missingFields: ['Endpoint', 'ApiKey'],
      },
      {
        sectionName: 'AzureOpenAiImages',
        hasSecrets: true,
        readinessStatus: 'unconfigured',
        missingFields: ['Endpoint', 'ApiKey'],
      },
      {
        sectionName: 'AzureSpeechService',
        hasSecrets: true,
        readinessStatus: 'unconfigured',
        missingFields: ['Endpoint', 'ApiKey'],
      },
      {
        sectionName: 'AzureDocumentIntelligence',
        hasSecrets: true,
        readinessStatus: 'unconfigured',
        missingFields: ['Endpoint', 'ApiKey'],
      },
      {
        sectionName: 'GoogleGeminiApi',
        hasSecrets: true,
        readinessStatus: geminiApiKeyStored ? 'configured' : 'unconfigured',
        missingFields: geminiApiKeyStored ? [] : ['ApiKey'],
      },
      {
        sectionName: 'OpenAI',
        hasSecrets: true,
        readinessStatus: openAiApiKeyStored ? 'configured' : 'unconfigured',
        missingFields: openAiApiKeyStored ? [] : ['ApiKey'],
      },
      {
        sectionName: 'HuggingFace',
        hasSecrets: true,
        readinessStatus: hfTokenStored ? 'configured' : 'unconfigured',
        missingFields: hfTokenStored ? [] : ['Token'],
      },
      {
        sectionName: 'OpenRouter',
        hasSecrets: true,
        readinessStatus: openRouterApiKeyStored ? 'configured' : 'unconfigured',
        missingFields: openRouterApiKeyStored ? [] : ['ApiKey'],
      },
    ]);

    vi.mocked(api.settings.getSections).mockImplementation(async () => getSectionSummaries());
    vi.mocked(api.settings.getSchema).mockResolvedValue({
      sections: [
        {
          sectionName: 'AzureOpenAI',
          schemaVersion: 1,
          hasSecrets: true,
          properties: [
            { name: 'Resource', valueType: 'string', isSecret: false, isEditable: true, isRequired: true },
            { name: 'ApiKey', valueType: 'string', isSecret: true, isEditable: true, isRequired: true },
            {
              name: 'ApiVersion',
              valueType: 'string',
              isSecret: false,
              isEditable: true,
              isRequired: true,
              defaultValue: '2025-04-01-preview',
            },
          ],
        },
        {
          sectionName: 'AzureOpenAiImages',
          schemaVersion: 1,
          hasSecrets: true,
          properties: [
            {
              name: 'ApiVersion',
              valueType: 'string',
              isSecret: false,
              isEditable: true,
              isRequired: true,
              defaultValue: '2025-04-01-preview',
            },
          ],
        },
        {
          sectionName: 'GoogleGeminiApi',
          schemaVersion: 1,
          hasSecrets: true,
          properties: [
            {
              name: 'ApiKey',
              valueType: 'string',
              isSecret: true,
              isEditable: true,
              isRequired: true,
            },
          ],
        },
        {
          sectionName: 'OpenAI',
          schemaVersion: 1,
          hasSecrets: true,
          properties: [
            { name: 'ApiKey', valueType: 'string', isSecret: true, isEditable: true, isRequired: true },
            { name: 'Endpoint', valueType: 'string', isSecret: false, isEditable: true, isRequired: false },
          ],
        },
        {
          sectionName: 'HuggingFace',
          schemaVersion: 1,
          hasSecrets: true,
          properties: [
            { name: 'Token', valueType: 'string', isSecret: true, isEditable: true, isRequired: true },
            { name: 'RouterBaseUrl', valueType: 'string', isSecret: false, isEditable: true, isRequired: true },
          ],
        },
        {
          sectionName: 'OpenRouter',
          schemaVersion: 1,
          hasSecrets: true,
          properties: [
            { name: 'ApiKey', valueType: 'string', isSecret: true, isEditable: true, isRequired: true },
            { name: 'BaseUrl', valueType: 'string', isSecret: false, isEditable: true, isRequired: true },
          ],
        },
      ],
      services: [],
      providers: [],
      runtimeDependencies: [],
    });
    vi.mocked(api.settings.getModels).mockImplementation(async () => [...models]);
    vi.mocked(api.settings.getSection).mockImplementation(async (sectionName: string) => {
      const base = {
        sectionName,
        schemaVersion: 1,
        rowVersion: `${rowVersion}`,
        updatedUtc: NOW,
        payload: {},
        secretHasValue: {},
      };
      if (sectionName === 'AzureOpenAI') {
        return {
          ...base,
          payload: {
            Resource: coreResource,
            ApiKey: '',
            ApiVersion: coreApiVersion,
          },
          secretHasValue: {
            ApiKey: coreApiKeyStored,
          },
        };
      }
      if (sectionName === 'GoogleGeminiApi') {
        return {
          ...base,
          payload: {
            ApiKey: '',
          },
          secretHasValue: {
            ApiKey: geminiApiKeyStored,
          },
        };
      }
      if (sectionName === 'OpenAI') {
        return {
          ...base,
          payload: {
            ApiKey: '',
            Endpoint: openAiEndpoint,
          },
          secretHasValue: {
            ApiKey: openAiApiKeyStored,
          },
        };
      }
      if (sectionName === 'HuggingFace') {
        return {
          ...base,
          payload: {
            Token: '',
            RouterBaseUrl: hfRouterBaseUrl,
          },
          secretHasValue: {
            Token: hfTokenStored,
          },
        };
      }
      if (sectionName === 'OpenRouter') {
        return {
          ...base,
          payload: {
            ApiKey: '',
            BaseUrl: openRouterBaseUrl,
          },
          secretHasValue: {
            ApiKey: openRouterApiKeyStored,
          },
        };
      }
      return base;
    });
    vi.mocked(api.settings.updateSection).mockImplementation(async (sectionName: string, request: any) => {
      if (sectionName === 'AzureOpenAI') {
        const payload = request?.payload ?? {};
        coreResource = typeof payload.Resource === 'string' ? payload.Resource : coreResource;
        coreApiVersion = typeof payload.ApiVersion === 'string' ? payload.ApiVersion : coreApiVersion;
        if (typeof payload.ApiKey === 'string' && payload.ApiKey.trim().length > 0) {
          coreApiKeyStored = true;
        }
      }
      if (sectionName === 'GoogleGeminiApi') {
        const payload = request?.payload ?? {};
        if (typeof payload.ApiKey === 'string' && payload.ApiKey.trim().length > 0) {
          geminiApiKeyStored = true;
        }
      }
      if (sectionName === 'OpenAI') {
        const payload = request?.payload ?? {};
        if (typeof payload.ApiKey === 'string' && payload.ApiKey.trim().length > 0) {
          openAiApiKeyStored = true;
        }
        if (typeof payload.Endpoint === 'string') {
          openAiEndpoint = payload.Endpoint;
        }
      }
      if (sectionName === 'HuggingFace') {
        const payload = request?.payload ?? {};
        if (typeof payload.Token === 'string' && payload.Token.trim().length > 0) {
          hfTokenStored = true;
        }
        if (typeof payload.RouterBaseUrl === 'string' && payload.RouterBaseUrl.trim().length > 0) {
          hfRouterBaseUrl = payload.RouterBaseUrl;
        }
      }
      if (sectionName === 'OpenRouter') {
        const payload = request?.payload ?? {};
        if (typeof payload.ApiKey === 'string' && payload.ApiKey.trim().length > 0) {
          openRouterApiKeyStored = true;
        }
        if (typeof payload.BaseUrl === 'string' && payload.BaseUrl.trim().length > 0) {
          openRouterBaseUrl = payload.BaseUrl;
        }
      }
      rowVersion += 1;
      return {
        sectionName,
        schemaVersion: 1,
        rowVersion: `${rowVersion}`,
        updatedUtc: NOW,
        payload: request?.payload ?? {},
        secretHasValue: { ApiKey: sectionName === 'GoogleGeminiApi' ? geminiApiKeyStored : coreApiKeyStored },
      };
    });
    vi.mocked(api.settings.addModel).mockImplementation(async (request: any) => {
      const created = {
        modelId: request.catalog.modelId,
        displayName: request.catalog.displayName,
        provider: request.provider,
        isActive: true,
        created: NOW,
      };
      models = [...models, created];
      return {
        addOperation: {
          kind: 'sync',
          status: 'completed',
          catalogModel: created,
        },
      } as any;
    });
    vi.mocked(api.settings.chatDefaults.get).mockImplementation(async () => ({ ...chatDefaults }));
    vi.mocked(api.settings.chatDefaults.update).mockImplementation(async (request: any) => {
      chatDefaults = {
        rowVersion: `${Number(chatDefaults.rowVersion) + 1}`,
        defaultModelId: request.defaultModelId ?? null,
        overrideAllChatModels: request.overrideAllChatModels,
        temperature: request.temperature ?? null,
        topP: request.topP ?? null,
        reasoningEffort: request.reasoningEffort ?? null,
        samplingParametersJson: request.samplingParametersJson ?? null,
      };
      return { ...chatDefaults };
    });
    vi.mocked(api.settings.services.get).mockRejectedValue(new Error('service state not needed for this test'));
    vi.mocked(api.settings.services.updateProviderFields).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.services.updateActiveProvider).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.getRuntimeProfiles).mockResolvedValue([
      {
        profileId: 'qwen3_6',
        displayName: 'Qwen 3.6',
        providerKind: 'llama-cpp',
        description: 'Test profile',
        isDefault: true,
        samplingParametersJson: null,
        reasoningEffort: null,
      },
    ] as any);
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
    ] as any);
    vi.mocked(api.settings.localModels.cancelOperation).mockResolvedValue(undefined as never);
    vi.mocked(api.settings.localModels.getOperation).mockRejectedValue(new Error('not used'));
  });

  it('shows both provider paths in the first step', async () => {
    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    expect(within(providerSelect).getByRole('option', { name: 'Microsoft Foundry' })).toBeInTheDocument();
    expect(within(providerSelect).getByRole('option', { name: 'Google Gemini' })).toBeInTheDocument();
  });

  it('switches the wizard progress to service-specific Local AI steps when Local AI is selected', async () => {
    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'local-ai' } });

    expect(screen.getByText('Speech Transcription')).toBeInTheDocument();
    expect(screen.getByText('Image Generation')).toBeInTheDocument();
    expect(screen.getByText('Speech Synthesis')).toBeInTheDocument();
    expect(screen.getByText('Document Intelligence')).toBeInTheDocument();
    expect(screen.getByText('Embeddings')).toBeInTheDocument();
    expect(screen.queryByText('Optional services')).not.toBeInTheDocument();
  });

  it('auto-sets the first model as global default and allows finishing after one saved Foundry model', async () => {
    const onDismiss = vi.fn();

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    await screen.findByLabelText(/provider/i);
    expect(screen.getByRole('button', { name: 'Finish' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Microsoft Foundry connection details/i });
    fireEvent.change(screen.getByLabelText(/resource/i), { target: { value: 'my-foundry-resource' } });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'super-secret-key-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Models \(required\)/i });
    expect(screen.getByLabelText(/set this model as the global default chat model/i)).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gpt-4o' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    expect(screen.getByRole('button', { name: 'Finish' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
        expect.objectContaining({
          defaultModelId: 'gpt-4o',
          overrideAllChatModels: false,
        })
      )
    );

    await screen.findByRole('heading', { name: /Optional Microsoft Foundry services/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Finish setup/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('lets users choose a newly-added Foundry model as global default when Foundry models already exist', async () => {
    let models = [
      {
        modelId: 'existing-model',
        displayName: 'existing-model',
        provider: 'azure-openai-chat',
        isActive: true,
        created: NOW,
      },
    ];
    vi.mocked(api.settings.getModels).mockImplementation(async () => [...models]);
    vi.mocked(api.settings.addModel).mockImplementation(async (request: any) => {
      const created = {
        modelId: request.catalog.modelId,
        displayName: request.catalog.displayName,
        provider: request.provider,
        isActive: true,
        created: NOW,
      };
      models = [...models, created];
      return {
        addOperation: {
          kind: 'sync',
          status: 'completed',
          catalogModel: created,
        },
      } as any;
    });

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    await screen.findByLabelText(/provider/i);
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByRole('heading', { name: /Microsoft Foundry connection details/i });
    fireEvent.change(screen.getByLabelText(/resource/i), { target: { value: 'my-foundry-resource' } });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'super-secret-key-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Models \(required\)/i });
    const defaultCheckbox = screen.getByLabelText(/set this model as the global default chat model/i);
    expect(defaultCheckbox).toBeEnabled();
    fireEvent.click(defaultCheckbox);
    expect(defaultCheckbox).toBeChecked();

    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gpt-5' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    expect(screen.getByText('Global default')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.chatDefaults.update).toHaveBeenCalledWith(
        expect.objectContaining({
          defaultModelId: 'gpt-5',
          overrideAllChatModels: false,
        })
      )
    );
  });

  it('requires a Gemini model before enabling finish in Gemini flow', async () => {
    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'google-gemini' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Google Gemini connection details/i });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'gemini-secret-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Gemini models \(required\)/i });
    expect(screen.getByRole('button', { name: 'Finish' })).toBeDisabled();
    expect((screen.getByLabelText(/^Model$/i) as HTMLInputElement).value).toBe('gemini-2.5-flash');

    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gemini-2.5-flash' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.addModel).toHaveBeenCalledWith(
        expect.objectContaining({
          provider: 'google-gemini-chat',
          catalog: expect.objectContaining({ modelId: 'gemini-2.5-flash' }),
        })
      )
    );
  });

  it('validates Gemini speech synthesis fields before persisting optional services', async () => {
    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'google-gemini' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Google Gemini connection details/i });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'gemini-secret-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Gemini models \(required\)/i });
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gemini-2.5-pro' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Optional Google Gemini services/i });
    const synthesisHeader = screen.getByRole('heading', { name: 'Speech Synthesis' });
    const synthesisCard = synthesisHeader.closest('div')?.parentElement?.parentElement;
    expect(synthesisCard).not.toBeNull();
    if (!synthesisCard) {
      return;
    }

    expect((within(synthesisCard).getByLabelText('Configure now') as HTMLInputElement).checked).toBe(true);
    fireEvent.change(within(synthesisCard).getByLabelText(/TTS Model ID/i), { target: { value: 'gemini-3.1-flash-tts-preview' } });
    fireEvent.change(within(synthesisCard).getByLabelText(/Voice Name/i), { target: { value: '' } });
    fireEvent.change(within(synthesisCard).getByLabelText(/Timeout Seconds/i), { target: { value: '300' } });

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText(/Voice name is required/i)).toBeInTheDocument();
    expect(api.settings.services.updateProviderFields).not.toHaveBeenCalledWith(
      'SpeechSynthesis',
      'SpeechSynthesis.Google.TextToSpeech',
      expect.anything()
    );

    fireEvent.change(within(synthesisCard).getByLabelText(/Voice Name/i), { target: { value: 'Kore' } });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
        'SpeechSynthesis',
        'SpeechSynthesis.Google.TextToSpeech',
        expect.objectContaining({
          ModelId: 'gemini-3.1-flash-tts-preview',
          VoiceName: 'Kore',
          TimeoutSeconds: '300',
        })
      )
    );
    await waitFor(() =>
      expect(api.settings.services.updateActiveProvider).toHaveBeenCalledWith(
        'SpeechSynthesis',
        'SpeechSynthesis.Google.TextToSpeech'
      )
    );
  });

  it('prefills Gemini optional service defaults as actual field values', async () => {
    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={vi.fn()}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'google-gemini' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Google Gemini connection details/i });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'gemini-secret-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Gemini models \(required\)/i });
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gemini-2.5-pro' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Optional Google Gemini services/i });

    const embeddingsCard = screen.getByRole('heading', { name: 'Embeddings' }).closest('div')?.parentElement?.parentElement;
    expect(embeddingsCard).not.toBeNull();
    if (!embeddingsCard) {
      return;
    }
    expect((within(embeddingsCard).getByLabelText('Configure now') as HTMLInputElement).checked).toBe(true);
    expect((within(embeddingsCard).getByLabelText(/Embedding Model ID/i) as HTMLInputElement).value).toBe('gemini-embedding-2');
    expect((within(embeddingsCard).getByLabelText(/Timeout Seconds/i) as HTMLInputElement).value).toBe('300');

    const imagesCard = screen.getByRole('heading', { name: 'Image Generation' }).closest('div')?.parentElement?.parentElement;
    expect(imagesCard).not.toBeNull();
    if (!imagesCard) {
      return;
    }
    expect((within(imagesCard).getByLabelText('Configure now') as HTMLInputElement).checked).toBe(true);
    expect((within(imagesCard).getByLabelText(/Image Model ID/i) as HTMLInputElement).value).toBe('gemini-2.5-flash-image');
    expect((within(imagesCard).getByLabelText(/Timeout Seconds/i) as HTMLInputElement).value).toBe('900');

    const transcriptionCard = screen.getByRole('heading', { name: 'Speech Transcription' }).closest('div')?.parentElement?.parentElement;
    expect(transcriptionCard).not.toBeNull();
    if (!transcriptionCard) {
      return;
    }
    expect((within(transcriptionCard).getByLabelText('Configure now') as HTMLInputElement).checked).toBe(true);
    expect((within(transcriptionCard).getByLabelText(/Transcription Model ID/i) as HTMLInputElement).value).toBe('gemini-2.5-flash');
    expect((within(transcriptionCard).getByLabelText(/Timeout Seconds/i) as HTMLInputElement).value).toBe('300');

    const synthesisCard = screen.getByRole('heading', { name: 'Speech Synthesis' }).closest('div')?.parentElement?.parentElement;
    expect(synthesisCard).not.toBeNull();
    if (!synthesisCard) {
      return;
    }
    expect((within(synthesisCard).getByLabelText('Configure now') as HTMLInputElement).checked).toBe(true);
    expect((within(synthesisCard).getByLabelText(/TTS Model ID/i) as HTMLInputElement).value).toBe('gemini-3.1-flash-tts-preview');
    expect((within(synthesisCard).getByLabelText(/Voice Name/i) as HTMLInputElement).value).toBe('Kore');
    expect((within(synthesisCard).getByLabelText(/Timeout Seconds/i) as HTMLInputElement).value).toBe('300');
  });

  it('routes Finish to the Finish step before closing from Gemini optional services', async () => {
    const onDismiss = vi.fn();

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'google-gemini' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Google Gemini connection details/i });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'gemini-secret-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Gemini models \(required\)/i });
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gemini-2.5-pro' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Optional Google Gemini services/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));

    await screen.findByRole('heading', { name: /Finish setup/i });
    expect(onDismiss).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('completes the OpenAI provider happy path through finish', async () => {
    const onDismiss = vi.fn();

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'openai' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /OpenAI connection details/i });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'openai-secret-key-12345' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /OpenAI models \(required\)/i });
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'gpt-4.1-mini' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.addModel).toHaveBeenCalledWith(
        expect.objectContaining({
          provider: 'openai-chat',
          catalog: expect.objectContaining({ modelId: 'gpt-4.1-mini' }),
        })
      )
    );

    await screen.findByRole('heading', { name: /Optional OpenAI services/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
        'SpeechTranscription',
        OPENAI_SERVICE_PROVIDER_IDS.SpeechTranscription,
        expect.objectContaining({ ModelId: 'whisper-1' })
      )
    );

    await screen.findByRole('heading', { name: /Finish setup/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('completes the Hugging Face provider happy path through finish', async () => {
    const onDismiss = vi.fn();

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'huggingface' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Hugging Face connection details/i });
    fireEvent.change(screen.getByLabelText(/^Token$/i), { target: { value: 'hf-secret-token-12345' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Hugging Face chat models \(required\)/i });
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'zai-org/GLM-5.2' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.addModel).toHaveBeenCalledWith(
        expect.objectContaining({
          provider: 'hf-inference-chat',
          catalog: expect.objectContaining({ modelId: 'zai-org/GLM-5.2' }),
        })
      )
    );

    await screen.findByRole('heading', { name: /Optional Hugging Face services/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
        'Embeddings',
        HUGGINGFACE_SERVICE_PROVIDER_IDS.Embeddings,
        expect.objectContaining({ ModelId: 'microsoft/harrier-oss-v1-0.6b' })
      )
    );

    await screen.findByRole('heading', { name: /Finish setup/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('completes the OpenRouter provider happy path through finish', async () => {
    const onDismiss = vi.fn();

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'openrouter' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /OpenRouter connection details/i });
    fireEvent.change(screen.getByLabelText(/api key/i), { target: { value: 'openrouter-secret-key-12345' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /OpenRouter models \(required\)/i });
    fireEvent.change(screen.getByLabelText(/^Model$/i), { target: { value: 'minimax/minimax-m3' } });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.addModel).toHaveBeenCalledWith(
        expect.objectContaining({
          provider: 'openrouter-chat',
          catalog: expect.objectContaining({ modelId: 'minimax/minimax-m3' }),
        })
      )
    );

    await screen.findByRole('heading', { name: /Optional OpenRouter services/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await waitFor(() =>
      expect(api.settings.services.updateProviderFields).toHaveBeenCalledWith(
        'Embeddings',
        OPENROUTER_SERVICE_PROVIDER_IDS.Embeddings,
        expect.objectContaining({ ModelId: 'nvidia/llama-nemotron-embed-vl-1b-v2:free' })
      )
    );

    await screen.findByRole('heading', { name: /Finish setup/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('completes the Local AI happy path with an existing catalog model and skipped service steps', async () => {
    const onDismiss = vi.fn();
    vi.mocked(api.settings.getModels).mockResolvedValue([
      {
        modelId: 'qwen3-local',
        displayName: 'Qwen 3 Local',
        provider: 'llama-cpp',
        isActive: true,
        created: NOW,
      },
    ]);

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'local-ai' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Local AI Prerequisites/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Local Chat Models/i });
    expect(screen.getByText('qwen3-local')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    for (let index = 0; index < 5; index += 1) {
      const skipButton = await screen.findByRole('button', { name: 'Skip this service' });
      fireEvent.click(skipButton);
    }

    await screen.findByRole('heading', { name: /Finish setup/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('installs a local model via existing alias and reaches the finish step', async () => {
    const onDismiss = vi.fn();
    vi.mocked(api.settings.addModel).mockImplementation(async (request: any) => ({
      operationId: undefined,
      addOperation: {
        kind: 'sync',
        status: 'completed',
        catalogModel: {
          modelId: request.catalog.modelId,
          displayName: request.catalog.displayName,
          provider: request.provider,
          isActive: true,
          created: NOW,
        },
      },
    } as any));

    render(
      <AddAiServicesWizard
        isOpen={true}
        onDismiss={onDismiss}
        onOpenSettings={vi.fn()}
      />
    );

    const providerSelect = await screen.findByLabelText(/provider/i);
    fireEvent.change(providerSelect, { target: { value: 'local-ai' } });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Local AI Prerequisites/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Local Chat Models/i });
    await waitFor(() => expect(api.settings.getRuntimeProfiles).toHaveBeenCalled());

    const installSection = screen.getByText('Install a model').closest('.rounded.border') as HTMLElement;
    const [installSourceSelect] = within(installSection).getAllByRole('combobox');
    fireEvent.change(installSourceSelect, { target: { value: 'existingAlias' } });
    await waitFor(() => {
      const controls = within(installSection).getAllByRole('combobox');
      expect(controls).toHaveLength(3);
    });
    const [, runtimeProfileSelect, existingAliasSelect] = within(installSection).getAllByRole('combobox');
    fireEvent.change(runtimeProfileSelect, { target: { value: 'qwen3_6' } });
    fireEvent.change(existingAliasSelect, { target: { value: 'qwen3-local' } });
    fireEvent.click(screen.getByRole('button', { name: /Install model/i }));

    await waitFor(() =>
      expect(api.settings.addModel).toHaveBeenCalledWith(
        expect.objectContaining({
          provider: 'llama-cpp',
          catalog: expect.objectContaining({ modelId: 'qwen3-local' }),
        })
      )
    );

    await waitFor(() => expect(screen.getByText('Installed')).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    for (let index = 0; index < 5; index += 1) {
      const skipButton = await screen.findByRole('button', { name: 'Skip this service' });
      fireEvent.click(skipButton);
    }

    await screen.findByRole('heading', { name: /Finish setup/i });
    fireEvent.click(screen.getByRole('button', { name: 'Finish' }));
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });
});
