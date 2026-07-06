import { createRef } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ProviderEditorStateDto } from '../../../../../types/settings';
import { LOCAL_AI_SERVICE_PROVIDER_IDS } from '../../constants';
import { DraftProgress, LocalAiModelsStep } from '../LocalAiModelsStep';
import { LocalAiPrerequisitesStep } from '../LocalAiPrerequisitesStep';
import { LocalAiServiceStepBase, type LocalAiServiceStepHandle } from '../LocalAiServiceStepBase';
import { createLocalAiModelDraft, createLocalAiPrereqsForm } from './stepTestHelpers';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        listOutcome: vi.fn(),
      },
    },
  },
}));

vi.mock('../../../../../pages/settings/editors/common', async () => {
  const actual = await vi.importActual<typeof import('../../../../../pages/settings/editors/common')>(
    '../../../../../pages/settings/editors/common',
  );
  return {
    ...actual,
    RepositoryFilePicker: ({
      onRepositoryChange,
      onChange,
    }: {
      onRepositoryChange: (value: string) => void;
      onChange: (values: Record<string, string>) => void;
    }) => (
      <div data-testid="repo-picker">
        <input
          data-testid="repo-input"
          aria-label="Repository"
          onChange={(event) => onRepositoryChange(event.target.value)}
        />
        <button
          type="button"
          onClick={() => onChange({ 'llamaCpp.model': 'Qwen3-9B-Q5_K_M.gguf' })}
        >
          Pick quant
        </button>
      </div>
    ),
  };
});

const mockSave = vi.fn();
const mockSwitchProvider = vi.fn();
const mockSetDraftForProvider = vi.fn();
const mockClearFieldError = vi.fn();

function makeLocalProvider(providerId: string): ProviderEditorStateDto {
  return {
    providerId,
    providerKind: 'Local',
    displayName: providerId,
    providerSection: 'LocalEmb',
    modeId: null,
    hasExplicitMode: true,
    isDefaultMode: true,
    connectionConfigured: true,
    connectionMissingFields: [],
    canActivate: true,
    activationBlockers: [],
    fields: {
      Endpoint: { name: 'Endpoint', value: 'http://localhost:8100', isSecret: false, hasValue: true },
    },
    runtimeDependencies: [
      { key: 'EmbeddingsBaseUrl', hasValue: true, currentValue: 'http://localhost:8100' },
    ],
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
  };
}

vi.mock('../../../../../pages/settings/state/useServiceEditorController', () => ({
  useServiceEditorController: vi.fn(() => ({
    state: {
      serviceId: 'Embeddings',
      activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
      providers: [makeLocalProvider(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings)],
      readiness: { status: 'ready', blockers: [], warnings: ['Check disk space'] },
    },
    loading: false,
    error: null,
    fieldErrors: {},
    draft: {
      activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
      draftsByProvider: { [LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings]: {} },
      switchProvider: mockSwitchProvider,
      setDraftForProvider: mockSetDraftForProvider,
    },
    save: mockSave,
    clearFieldError: mockClearFieldError,
  })),
}));

import { api } from '../../../../../services/api';
import { useServiceEditorController } from '../../../../../pages/settings/state/useServiceEditorController';

describe('LocalAiPrerequisitesStep', () => {
  const onChange = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.localModels.listOutcome).mockResolvedValue({
      kind: 'available',
      payload: { items: [{ id: 'model-1' }] },
    });
  });

  it('renders prerequisites heading and token field', () => {
    render(
      <LocalAiPrerequisitesStep
        value={createLocalAiPrereqsForm()}
        errors={{}}
        onChange={onChange}
        localChatModelCount={0}
      />,
    );

    expect(screen.getByText('Local AI Prerequisites')).toBeInTheDocument();
    expect(screen.getByText(/No local chat model configured/)).toBeInTheDocument();
    expect(screen.getByDisplayValue('')).toBeInTheDocument();
  });

  it('calls onChange when hugging face token is edited', () => {
    render(
      <LocalAiPrerequisitesStep
        value={createLocalAiPrereqsForm()}
        errors={{}}
        onChange={onChange}
        localChatModelCount={0}
      />,
    );

    const tokenInput = document.querySelector('input[type="password"]') as HTMLInputElement;
    fireEvent.change(tokenInput, { target: { value: 'hf_secret' } });
    expect(onChange).toHaveBeenCalledWith({ huggingFaceToken: 'hf_secret' });
  });

  it('shows stored token hint, validation error, and configured chat models', async () => {
    render(
      <LocalAiPrerequisitesStep
        value={createLocalAiPrereqsForm({ huggingFaceTokenHasStoredValue: true })}
        errors={{ huggingFaceToken: 'Token required' }}
        onChange={onChange}
        localChatModelCount={2}
      />,
    );

    expect(screen.getByText(/A token is already stored/)).toBeInTheDocument();
    expect(screen.getByText('Token required')).toBeInTheDocument();
    expect(screen.getByText('2 installed')).toBeInTheDocument();
    expect(screen.queryByText(/No local chat model configured/)).not.toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getAllByText('Configured').length).toBeGreaterThan(0);
    });
  });
});

function selectContainingOption(optionName: string | RegExp): HTMLSelectElement {
  const option = screen.getByRole('option', { name: optionName });
  const select = option.closest('select');
  if (!select) {
    throw new Error(`Select not found for option: ${String(optionName)}`);
  }
  return select;
}

describe('LocalAiModelsStep', () => {
  const onInstall = vi.fn().mockResolvedValue(undefined);
  const onRemoveDraft = vi.fn();

  const defaultInventoryItem = {
    routerModelId: 'orphan-alias',
    runtimeState: 'unloaded',
    hasModelFile: true,
    hasMmprojFile: false,
    catalogModelIds: [] as string[],
    notebookReferenceCount: 0,
  };

  const defaultProps = {
    draftModels: [],
    existingModels: [],
    profiles: [{
      profileId: 'default',
      displayName: 'Default',
      combineSystemAndDeveloperMessages: false,
      samplingParametersJson: '{}',
      thinkingControlJson: '{}',
      providers: ['llama-cpp'],
      created: '2026-01-01T00:00:00Z',
    }],
    profilesLoading: false,
    inventory: [defaultInventoryItem],
    inventoryLoading: false,
    installError: null,
    installModelError: null,
    onInstall,
    onRemoveDraft,
  };

  beforeEach(() => {
    vi.clearAllMocks();
    onInstall.mockResolvedValue(undefined);
  });

  it('renders install form and empty-state warning', () => {
    render(<LocalAiModelsStep {...defaultProps} />);

    expect(screen.getByText('Local Chat Models')).toBeInTheDocument();
    expect(screen.getByText(/At least one model must be installed/)).toBeInTheDocument();
    expect(selectContainingOption('Install from Hugging Face')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Install model' })).toBeInTheDocument();
  });

  it('shows existing models and draft install progress', () => {
    render(
      <LocalAiModelsStep
        {...defaultProps}
        existingModels={[{ modelId: 'qwen3-9b', displayName: 'Qwen3', provider: 'llama-cpp', isActive: true, created: '' }]}
        draftModels={[createLocalAiModelDraft({ asyncStatus: 'downloading', asyncProgress: 0.42 })]}
      />,
    );

    expect(screen.getAllByText('qwen3-9b').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Downloading')).toBeInTheDocument();
    expect(screen.getByText('42%')).toBeInTheDocument();
    expect(screen.queryByText(/At least one model must be installed/)).not.toBeInTheDocument();
  });

  it('submits huggingface install form via onInstall', async () => {
    render(<LocalAiModelsStep {...defaultProps} />);

    fireEvent.change(selectContainingOption(/Default/), { target: { value: 'default' } });
    fireEvent.change(screen.getByPlaceholderText('e.g. qwen3-9b'), { target: { value: 'qwen3-9b' } });
    fireEvent.change(screen.getByTestId('repo-input'), { target: { value: 'Qwen/Qwen3-9B' } });
    fireEvent.click(screen.getByRole('button', { name: 'Pick quant' }));
    fireEvent.click(screen.getByRole('button', { name: 'Install model' }));

    await waitFor(() => {
      expect(onInstall).toHaveBeenCalledWith(
        expect.objectContaining({
          installSource: 'huggingface',
          routerModelId: 'qwen3-9b',
          runtimeProfileId: 'default',
          huggingFaceRepository: 'Qwen/Qwen3-9B',
          huggingFaceQuantIncludePattern: 'Qwen3-9B-Q5_K_M.gguf',
          setAsGlobalDefault: true,
        }),
      );
    });
  });

  it('switches to existing alias source and removes errored draft on dismiss', () => {
    const erroredDraft = createLocalAiModelDraft({
      localId: 'err-draft',
      asyncStatus: 'error',
      asyncError: 'Download failed',
    });

    render(
      <LocalAiModelsStep
        {...defaultProps}
        draftModels={[erroredDraft]}
        inventory={[{ ...defaultInventoryItem, routerModelId: 'alias-1' }]}
      />,
    );

    fireEvent.change(selectContainingOption('Install from Hugging Face'), {
      target: { value: 'existingAlias' },
    });
    expect(selectContainingOption('alias-1')).toBeInTheDocument();

    const installingCard = screen.getByText('Download failed').closest('.rounded-lg') as HTMLElement;
    fireEvent.click(within(installingCard).getByTitle('Dismiss'));
    expect(onRemoveDraft).toHaveBeenCalledWith('err-draft');
  });

  it('shows install errors', () => {
    render(
      <LocalAiModelsStep
        {...defaultProps}
        installError="Profile required"
        installModelError={{ code: 'VALIDATION', message: 'Missing fields', remediation: 'Select a profile' }}
      />,
    );

    expect(screen.getByText('Profile required')).toBeInTheDocument();
    expect(screen.getByText('VALIDATION')).toBeInTheDocument();
    expect(screen.getByText('Select a profile')).toBeInTheDocument();
  });

  it('submits existing alias install form', async () => {
    render(
      <LocalAiModelsStep
        {...defaultProps}
        inventory={[{ ...defaultInventoryItem, routerModelId: 'alias-1' }]}
      />,
    );

    fireEvent.change(selectContainingOption('Install from Hugging Face'), {
      target: { value: 'existingAlias' },
    });
    fireEvent.change(selectContainingOption(/Default/), { target: { value: 'default' } });
    fireEvent.change(selectContainingOption('alias-1'), { target: { value: 'alias-1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Install model' }));

    await waitFor(() => {
      expect(onInstall).toHaveBeenCalledWith(
        expect.objectContaining({
          installSource: 'existingAlias',
          routerModelId: 'alias-1',
          existingAliasRouterModelId: 'alias-1',
        }),
      );
    });
  });

  it('shows llama unavailable message when inventory is empty', () => {
    render(<LocalAiModelsStep {...defaultProps} inventory={[]} inventoryLoading={false} />);

    fireEvent.change(selectContainingOption('Install from Hugging Face'), {
      target: { value: 'existingAlias' },
    });
    expect(screen.getByText(/No local llama server is reachable/)).toBeInTheDocument();
  });

  it('retries errored draft by repopulating the install form', async () => {
    const erroredDraft = createLocalAiModelDraft({
      localId: 'retry-draft',
      asyncStatus: 'error',
      routerModelId: 'retry-model',
      catalogModelId: 'retry-model',
      huggingFaceRepository: 'Org/Model',
    });

    render(
      <LocalAiModelsStep
        {...defaultProps}
        draftModels={[erroredDraft]}
      />,
    );

    const card = screen.getByText('retry-model').closest('.rounded-lg') as HTMLElement;
    fireEvent.click(within(card).getByTitle('Retry'));
    expect(onRemoveDraft).toHaveBeenCalledWith('retry-draft');
    expect(selectContainingOption('Install from Hugging Face')).toHaveValue('huggingface');
    expect(screen.getByPlaceholderText('e.g. qwen3-9b')).toHaveValue('retry-model');
  });

  it('shows global default checkbox when models already exist', async () => {
    const user = userEvent.setup();
    render(
      <LocalAiModelsStep
        {...defaultProps}
        existingModels={[{ modelId: 'qwen3-9b', displayName: 'Qwen3', provider: 'llama-cpp', isActive: true, created: '' }]}
      />,
    );

    const checkbox = screen.getByRole('checkbox');
    expect(checkbox).not.toBeChecked();
    await user.click(checkbox);
    expect(checkbox).toBeChecked();

    fireEvent.change(selectContainingOption(/Default/), { target: { value: 'default' } });
    fireEvent.change(screen.getByPlaceholderText('e.g. qwen3-9b'), { target: { value: 'another-model' } });
    fireEvent.change(screen.getByTestId('repo-input'), { target: { value: 'Qwen/Qwen3-9B' } });
    await user.click(screen.getByRole('button', { name: 'Pick quant' }));
    await user.click(screen.getByRole('button', { name: 'Install model' }));

    await waitFor(() => {
      expect(onInstall).toHaveBeenCalledWith(
        expect.objectContaining({
          routerModelId: 'another-model',
          setAsGlobalDefault: true,
        }),
      );
    });
  });

  it('resets install form after a draft completes', async () => {
    const completedDraft = createLocalAiModelDraft({
      localId: 'done-draft',
      asyncStatus: 'completed',
      routerModelId: 'done-model',
    });
    const { rerender } = render(
      <LocalAiModelsStep
        {...defaultProps}
        draftModels={[createLocalAiModelDraft({ localId: 'pending', asyncStatus: 'downloading' })]}
      />,
    );

    fireEvent.change(screen.getByPlaceholderText('e.g. qwen3-9b'), { target: { value: 'pending-model' } });
    rerender(<LocalAiModelsStep {...defaultProps} draftModels={[completedDraft]} />);

    await waitFor(() => {
      expect(screen.getByPlaceholderText('e.g. qwen3-9b')).toHaveValue('');
    });
  });
});

describe('DraftProgress', () => {
  it('renders completed and error states', () => {
    const { rerender } = render(<DraftProgress draft={createLocalAiModelDraft({ asyncStatus: 'completed', setAsGlobalDefault: true })} />);
    expect(screen.getByText('Installed')).toBeInTheDocument();
    expect(screen.getByText(/set as default/)).toBeInTheDocument();

    rerender(<DraftProgress draft={createLocalAiModelDraft({ asyncStatus: 'error', asyncError: 'Boom' })} />);
    expect(screen.getByText('Boom')).toBeInTheDocument();

    rerender(<DraftProgress draft={createLocalAiModelDraft({ asyncStatus: 'submitted' })} />);
    expect(screen.getByText(/Submitting to server/)).toBeInTheDocument();

    rerender(<DraftProgress draft={createLocalAiModelDraft({ asyncStatus: 'queued' })} />);
    expect(screen.getByText('Queued')).toBeInTheDocument();

    rerender(
      <DraftProgress
        draft={createLocalAiModelDraft({ asyncStatus: 'downloading', asyncProgress: 0.75 })}
      />,
    );
    expect(screen.getByText('75%')).toBeInTheDocument();
    expect(screen.getByText('Downloading')).toBeInTheDocument();
  });
});

describe('LocalAiServiceStepBase', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockSave.mockResolvedValue(true);
    vi.mocked(useServiceEditorController).mockReturnValue({
      state: {
        serviceId: 'Embeddings',
        activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
        providers: [makeLocalProvider(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings)],
        readiness: { status: 'ready', blockers: [], warnings: ['Check disk space'] },
      },
      loading: false,
      error: null,
      fieldErrors: {},
      draft: {
        activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
        draftsByProvider: { [LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings]: {} },
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave,
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);
  });

  it('renders ready state with provider fields and warnings', () => {
    render(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );

    expect(screen.getByText('Local Embeddings')).toBeInTheDocument();
    expect(screen.getByText(/Readiness: Ready/)).toBeInTheDocument();
    expect(screen.getByText('Check disk space')).toBeInTheDocument();
    expect(screen.getByDisplayValue('http://localhost:8100')).toBeInTheDocument();
    expect(screen.getByText('Operational Dependencies')).toBeInTheDocument();
  });

  it('patches provider field via ProviderFieldsSection interaction', () => {
    render(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );

    fireEvent.change(screen.getByDisplayValue('http://localhost:8100'), {
      target: { value: 'http://localhost:8200' },
    });
    expect(mockSetDraftForProvider).toHaveBeenCalledWith(
      LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
      { Endpoint: 'http://localhost:8200' },
    );
    expect(mockClearFieldError).toHaveBeenCalled();
  });

  it('exposes persist handle that saves provider configuration', async () => {
    const ref = createRef<LocalAiServiceStepHandle>();
    render(
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );

    await ref.current?.persist();
    expect(mockSave).toHaveBeenCalled();
  });

  it('shows loading and error states from controller', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      state: null,
      loading: true,
      error: null,
      fieldErrors: {},
      draft: {
        activeProviderId: '',
        draftsByProvider: {},
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave,
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);

    const { rerender } = render(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );
    expect(screen.getByText(/Loading Local Embeddings settings/)).toBeInTheDocument();

    vi.mocked(useServiceEditorController).mockReturnValue({
      state: null,
      loading: false,
      error: 'Service unavailable',
      fieldErrors: {},
      draft: {
        activeProviderId: '',
        draftsByProvider: {},
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave,
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);

    rerender(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );
    expect(screen.getByText('Service unavailable')).toBeInTheDocument();
  });

  it('shows blocked readiness when runtime is not ready', () => {
    render(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
        requireRuntimeReadiness
        runtimeReadiness={{ ready: false, status: 'starting', detail: 'Container warming up' }}
      />,
    );

    expect(screen.getByText(/Readiness: Blocked/)).toBeInTheDocument();
    expect(screen.getByText('Container warming up')).toBeInTheDocument();
  });

  it('shows service readiness blockers and runtime pending states', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      state: {
        serviceId: 'Embeddings',
        activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
        providers: [makeLocalProvider(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings)],
        readiness: { status: 'blocked', blockers: ['Endpoint unreachable'], warnings: [] },
      },
      loading: false,
      error: null,
      fieldErrors: {},
      draft: {
        activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
        draftsByProvider: { [LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings]: {} },
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave,
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);

    const { rerender } = render(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
        requireRuntimeReadiness
        runtimeReadiness={null}
      />,
    );

    expect(screen.getByText('Endpoint unreachable')).toBeInTheDocument();
    expect(screen.getByText(/Checking local runtime/)).toBeInTheDocument();

    rerender(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
        requireRuntimeReadiness
      />,
    );
    expect(screen.getByText(/Runtime signal unavailable/)).toBeInTheDocument();
  });

  it('switches provider on mount when draft active provider differs', () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      state: {
        serviceId: 'Embeddings',
        activeProviderId: 'other-provider',
        providers: [makeLocalProvider(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings)],
        readiness: { status: 'ready', blockers: [], warnings: [] },
      },
      loading: false,
      error: null,
      fieldErrors: {},
      draft: {
        activeProviderId: 'other-provider',
        draftsByProvider: { [LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings]: {} },
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave,
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);

    render(
      <LocalAiServiceStepBase
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );

    expect(mockSwitchProvider).toHaveBeenCalledWith(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings);
  });

  it('persist handle does not block navigation when runtime is not ready (optional service)', async () => {
    mockSave.mockResolvedValueOnce(true);
    const ref = createRef<LocalAiServiceStepHandle>();
    render(
      <LocalAiServiceStepBase
        ref={ref}
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
        requireRuntimeReadiness
        runtimeReadiness={{ ready: false, status: 'offline', detail: '' }}
      />,
    );

    // Runtime readiness (model loaded + warmed) is optional and must not block the
    // wizard; persist proceeds to save the (valid) provider config instead of throwing.
    await expect(ref.current?.persist()).resolves.toBeUndefined();
    expect(mockSave).toHaveBeenCalled();
  });

  it('persist handle rejects missing provider and failed save', async () => {
    vi.mocked(useServiceEditorController).mockReturnValue({
      state: {
        serviceId: 'Embeddings',
        activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
        providers: [],
        readiness: { status: 'ready', blockers: [], warnings: [] },
      },
      loading: false,
      error: null,
      fieldErrors: {},
      draft: {
        activeProviderId: LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings,
        draftsByProvider: {},
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave,
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);

    const missingProviderRef = createRef<LocalAiServiceStepHandle>();
    render(
      <LocalAiServiceStepBase
        ref={missingProviderRef}
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );
    await expect(missingProviderRef.current?.persist()).rejects.toThrow(/not available for Local Embeddings/);

    vi.mocked(useServiceEditorController).mockReturnValue({
      state: {
        serviceId: 'Embeddings',
        activeProviderId: 'other-provider',
        providers: [makeLocalProvider(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings)],
        readiness: { status: 'ready', blockers: [], warnings: [] },
      },
      loading: false,
      error: null,
      fieldErrors: {},
      draft: {
        activeProviderId: 'other-provider',
        draftsByProvider: { [LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings]: {} },
        switchProvider: mockSwitchProvider,
        setDraftForProvider: mockSetDraftForProvider,
      },
      save: mockSave.mockResolvedValueOnce(false),
      clearFieldError: mockClearFieldError,
    } as ReturnType<typeof useServiceEditorController>);

    const failedSaveRef = createRef<LocalAiServiceStepHandle>();
    render(
      <LocalAiServiceStepBase
        ref={failedSaveRef}
        serviceId="Embeddings"
        title="Local Embeddings"
        description="Configure local embeddings endpoint."
        localProviderId={LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings}
      />,
    );
    await expect(failedSaveRef.current?.persist()).rejects.toThrow(/Fix Local Embeddings fields/);
    expect(mockSwitchProvider).toHaveBeenCalledWith(LOCAL_AI_SERVICE_PROVIDER_IDS.Embeddings);
  });
});
