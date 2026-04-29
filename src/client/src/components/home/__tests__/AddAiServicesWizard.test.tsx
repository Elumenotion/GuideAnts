import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import AddAiServicesWizard from '../AddAiServicesWizard';
import { api } from '../../../services/api';

vi.mock('../../../services/api', () => ({
  api: {
    settings: {
      getSections: vi.fn(),
      getSchema: vi.fn(),
      getModels: vi.fn(),
      getSection: vi.fn(),
      updateSection: vi.fn(),
      addModel: vi.fn(),
      chatDefaults: {
        get: vi.fn(),
        update: vi.fn(),
      },
      services: {
        get: vi.fn(),
        updateProviderFields: vi.fn(),
        updateActiveProvider: vi.fn(),
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
      rowVersion += 1;
      return {
        sectionName,
        schemaVersion: 1,
        rowVersion: `${rowVersion}`,
        updatedUtc: NOW,
        payload: request?.payload ?? {},
        secretHasValue: { ApiKey: coreApiKeyStored },
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
  });

  it('auto-sets the first model as global default and allows finishing after one saved model', async () => {
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

    await screen.findByRole('heading', { name: /Microsot Foundry connection details/i });
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

    await screen.findByRole('heading', { name: /Optional Microsot Foundry services/i });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));

    await screen.findByRole('heading', { name: /Finish setup/i });
    const finishButton = screen.getByRole('button', { name: 'Finish' });
    expect(finishButton).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Done' })).not.toBeInTheDocument();

    fireEvent.click(finishButton);
    await waitFor(() => expect(onDismiss).toHaveBeenCalledWith(false));
  });

  it('lets users choose a newly-added model as global default when models already exist', async () => {
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
    await screen.findByRole('heading', { name: /Microsot Foundry connection details/i });
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
});
