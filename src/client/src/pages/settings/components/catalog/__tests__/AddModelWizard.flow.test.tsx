import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { AddModelWizard } from '../AddModelWizard';
import { api } from '../../../../../services/api';

import { catalogFixture } from '../../../../../features/localModelOnboarding/curated/fixtures';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      getModels: vi.fn(),
      addModel: vi.fn(),
      loadLlamaModel: vi.fn(),
      getDownloadStatus: vi.fn(),
      getLlamaCatalog: vi.fn(),
      getLlamaCatalogQuants: vi.fn(),
      getLlamaOperationStatus: vi.fn(),
      chatDefaults: {
        get: vi.fn(),
        update: vi.fn(),
      },
    },
  },
}));

vi.mock('../../../../../features/localModelOnboarding/useOperationPolling', () => ({
  useLocalModelOnboardingOperation: vi.fn(),
  useCuratedOperationPolling: vi.fn(),
}));

import { useLocalModelOnboardingOperation } from '../../../../../features/localModelOnboarding/useOperationPolling';

const mockUseOperation = vi.mocked(useLocalModelOnboardingOperation);

const mockApi = api as unknown as {
  settings: {
    getModels: ReturnType<typeof vi.fn>;
    addModel: ReturnType<typeof vi.fn>;
    loadLlamaModel: ReturnType<typeof vi.fn>;
  };
};

function catalogInputs() {
  const textboxes = screen.getAllByRole('textbox');
  return {
    modelId: textboxes[0]!,
    displayName: textboxes[1]!,
  };
}

const baseProps = {
  isOpen: true,
  providerPreselect: null as string | null,
  inventory: [],
  onClose: vi.fn(),
  onCatalogChanged: vi.fn(async () => {}),
  onSetActiveModelOperation: vi.fn(),
};

describe('AddModelWizard flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.settings.getModels.mockResolvedValue([]);
    mockApi.settings.addModel.mockResolvedValue({
      addOperation: { kind: 'sync' },
    });
    (api.settings.getLlamaCatalog as ReturnType<typeof vi.fn>).mockResolvedValue(catalogFixture);
  });

  it('walks provider through review and completes sync add', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const onCatalogChanged = vi.fn(async () => {});

    render(
      <AddModelWizard
        {...baseProps}
        onClose={onClose}
        onCatalogChanged={onCatalogChanged}
      />
    );

    await user.selectOptions(screen.getByRole('combobox'), 'openai-chat');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'my-model');
    await user.type(displayName, 'My Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => {
      expect(mockApi.settings.addModel).toHaveBeenCalled();
      expect(onCatalogChanged).toHaveBeenCalled();
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('validates duplicate model ids on blur', async () => {
    const user = userEvent.setup();
    mockApi.settings.getModels.mockResolvedValue([{ modelId: 'taken-id' }]);

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId } = catalogInputs();
    await user.type(modelId, 'taken-id');
    await user.tab();

    await waitFor(() => {
      expect(screen.getByText(/already exists/i)).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: 'Continue' })).toBeDisabled();
  });

  it('starts async operation and shows progress step', async () => {
    const user = userEvent.setup();
    const onSetActiveModelOperation = vi.fn();

    mockApi.settings.addModel.mockResolvedValue({
      operationId: 'op-1',
      addOperation: { kind: 'async', status: 'downloading' },
    });

    render(
      <AddModelWizard
        {...baseProps}
        providerPreselect="openai-chat"
        onSetActiveModelOperation={onSetActiveModelOperation}
      />
    );

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'async-model');
    await user.type(displayName, 'Async Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => {
      expect(screen.getByText('Queued')).toBeInTheDocument();
      expect(onSetActiveModelOperation).toHaveBeenCalledWith(
        expect.objectContaining({
          operationId: 'op-1',
          catalogModelId: 'async-model',
          kind: 'add',
          pollRoute: 'downloads',
        }),
      );
    });
  });

  it('shows structured submit error on review step', async () => {
    const user = userEvent.setup();

    mockApi.settings.addModel.mockRejectedValue({
      body: {
        code: 'VALIDATION_FAILED',
        step: 'catalog',
        message: 'Model id is invalid',
        remediation: 'Use lowercase letters only.',
      },
    });

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'bad-model');
    await user.type(displayName, 'Bad');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => {
      expect(screen.getByText('Model id is invalid')).toBeInTheDocument();
    });
  });

  it('opens directly on catalog step when provider is preselected', () => {
    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    expect(screen.getByText(/2 of 5 - Catalog entry/)).toBeInTheDocument();
  });

  it('navigates back through wizard steps', async () => {
    const user = userEvent.setup();

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'step-model');
    await user.type(displayName, 'Step Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    expect(screen.getByText(/4 of 5 - Review and create/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText(/3 of 5 - Provider configuration/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText(/2 of 5 - Catalog entry/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText(/1 of 5 - Choose provider/)).toBeInTheDocument();
  });

  it('shows generic submit error when API response is unstructured', async () => {
    const user = userEvent.setup();
    mockApi.settings.addModel.mockRejectedValue(new Error('Server exploded'));

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'boom-model');
    await user.type(displayName, 'Boom');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => {
      expect(screen.getByText('Server exploded')).toBeInTheDocument();
    });
  });

  it('reports model id validation failures from the API', async () => {
    const user = userEvent.setup();
    mockApi.settings.getModels.mockRejectedValue(new Error('offline'));

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId } = catalogInputs();
    await user.type(modelId, 'maybe-ok');
    await user.tab();

    await waitFor(() => {
      expect(screen.getByText(/could not validate model id/i)).toBeInTheDocument();
    });
  });

  it('shows progress download percentage and structured operation errors', async () => {
    const user = userEvent.setup();
    let onUpdate: ((op: { status: string; progress?: number | null; error?: unknown }) => void) | undefined;
    mockUseOperation.mockImplementation((opts) => {
      onUpdate = opts.onUpdate;
    });
    mockApi.settings.addModel.mockResolvedValue({
      operationId: 'op-progress',
      addOperation: { kind: 'async', status: 'downloading' },
    });

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'progress-model');
    await user.type(displayName, 'Progress Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => expect(screen.getByText('Queued')).toBeInTheDocument());
    onUpdate?.({
      status: 'downloading',
      progress: 0.42,
      error: {
        code: 'INSTALL_STEP_FAILED',
        step: 'downloading',
        message: 'Download stalled',
        remediation: 'Check disk space.',
      },
    });

    await waitFor(() => {
      expect(screen.getByText('42%')).toBeInTheDocument();
      expect(screen.getByText('Download stalled')).toBeInTheDocument();
      expect(screen.getByText('Check disk space.')).toBeInTheDocument();
    });
  });

  it('retries failed async operations from the progress step', async () => {
    const user = userEvent.setup();
    let onUpdate: ((op: { status: string }) => void) | undefined;
    mockUseOperation.mockImplementation((opts) => {
      onUpdate = opts.onUpdate;
    });
    mockApi.settings.addModel
      .mockResolvedValueOnce({
        operationId: 'op-fail',
        addOperation: { kind: 'async', status: 'downloading' },
      })
      .mockResolvedValueOnce({
        operationId: 'op-retry',
        addOperation: { kind: 'async', status: 'queued' },
      });

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'retry-model');
    await user.type(displayName, 'Retry Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => expect(screen.getByText('Queued')).toBeInTheDocument());
    onUpdate?.({ status: 'failed' });
    await waitFor(() => expect(screen.getByRole('button', { name: /Retry from failed step/i })).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: /Retry from failed step/i }));

    await waitFor(() => {
      expect(mockApi.settings.addModel).toHaveBeenCalledTimes(2);
      expect(screen.getByText('Queued')).toBeInTheDocument();
    });
  });

  it('refreshes catalog when async operation completes and supports poll failure', async () => {
    const user = userEvent.setup();
    const onCatalogChanged = vi.fn(async () => {});
    const onSetActiveModelOperation = vi.fn();
    let onTerminal: ((op: { status: string }) => void) | undefined;
    let onPollFailureThreshold: (() => void) | undefined;
    mockUseOperation.mockImplementation((opts) => {
      onTerminal = opts.onTerminal;
      onPollFailureThreshold = opts.onPollFailureThreshold;
    });
    mockApi.settings.addModel.mockResolvedValue({
      operationId: 'op-done',
      addOperation: { kind: 'async', status: 'registeringAlias' },
    });

    render(
      <AddModelWizard
        {...baseProps}
        providerPreselect="openai-chat"
        onCatalogChanged={onCatalogChanged}
        onSetActiveModelOperation={onSetActiveModelOperation}
      />
    );

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'done-model');
    await user.type(displayName, 'Done Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => expect(screen.getByText(/Registering alias/i)).toBeInTheDocument());
    onTerminal?.({ status: 'completed' });
    await waitFor(() => {
      expect(onCatalogChanged).toHaveBeenCalled();
      expect(onSetActiveModelOperation).toHaveBeenCalledWith(null);
    });

    onPollFailureThreshold?.();
    await waitFor(() => {
      expect(screen.getByText(/Failed to poll operation status/i)).toBeInTheDocument();
    });
  });

  it('errors when async add response is missing operation id', async () => {
    const user = userEvent.setup();
    mockApi.settings.addModel.mockResolvedValue({
      addOperation: { kind: 'async', status: 'downloading' },
    });

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'missing-op');
    await user.type(displayName, 'Missing Op');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => {
      expect(screen.getByText(/Missing operation id/i)).toBeInTheDocument();
    });
  });

  it('shows model chat behavior controls on provider config step', async () => {
    const user = userEvent.setup();

    render(
      <AddModelWizard
        {...baseProps}
        providerPreselect="openai-chat"
      />
    );

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'profile-model');
    await user.type(displayName, 'Profile Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    expect(screen.getByText(/3 of 5 - Provider configuration/)).toBeInTheDocument();
    expect(screen.getByText(/Sampling Parameters JSON/i)).toBeInTheDocument();
  });

  it('edits optional catalog metadata fields', async () => {
    const user = userEvent.setup();

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const textboxes = screen.getAllByRole('textbox');
    const description = textboxes[2]!;
    const displayOrder = screen.getByRole('spinbutton');
    const activeCheckbox = screen.getByRole('checkbox');

    await user.type(description, 'A helpful model');
    await user.clear(displayOrder);
    await user.type(displayOrder, '10');
    expect(activeCheckbox).toBeChecked();
    await user.click(activeCheckbox);
    expect(activeCheckbox).not.toBeChecked();
  });

  it('renders google gemini provider configuration copy', async () => {
    const user = userEvent.setup();

    render(<AddModelWizard {...baseProps} />);

    await user.selectOptions(screen.getByRole('combobox'), 'google-gemini-chat');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'gemini-model');
    await user.type(displayName, 'Gemini Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    expect(screen.getByText(/GoogleGeminiApi/i)).toBeInTheDocument();
  });

  it('shows llama install source on the review step in advanced mode', async () => {
    const user = userEvent.setup();

    render(
      <AddModelWizard
        {...baseProps}
        providerPreselect="llama-cpp"
      />
    );

    expect(screen.getByText(/2 of 4 - Provider configuration/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Custom Hugging Face/i }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    expect(screen.getByText(/Install Source:/i)).toBeInTheDocument();
    expect(screen.getByText('huggingface')).toBeInTheDocument();
  });

  it('renders anthropic provider configuration fields', async () => {
    const user = userEvent.setup();

    render(<AddModelWizard {...baseProps} />);

    await user.selectOptions(screen.getByRole('combobox'), 'anthropic');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'claude-model');
    await user.type(displayName, 'Claude Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));

    expect(screen.getByText(/3 of 5 - Provider configuration/)).toBeInTheDocument();
    expect(screen.getByText(/Sampling Parameters JSON/i)).toBeInTheDocument();
  });

  it('closes the wizard from the progress step footer', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    mockApi.settings.addModel.mockResolvedValue({
      operationId: 'op-close',
      addOperation: { kind: 'async', status: 'downloading' },
    });

    render(
      <AddModelWizard
        {...baseProps}
        providerPreselect="openai-chat"
        onClose={onClose}
      />
    );

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'close-model');
    await user.type(displayName, 'Close Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => expect(screen.getByText('Queued')).toBeInTheDocument());
    await user.click(screen.getByTitle('Close wizard'));
    expect(onClose).toHaveBeenCalled();
  });

  it('advances progress through registering alias and resolves completed operations', async () => {
    const user = userEvent.setup();
    let onUpdate: ((op: { status: string }) => void) | undefined;
    let onTerminal: ((op: { status: string }) => void) | undefined;
    mockUseOperation.mockImplementation((opts) => {
      onUpdate = opts.onUpdate;
      onTerminal = opts.onTerminal;
    });
    mockApi.settings.addModel.mockResolvedValue({
      operationId: 'op-reg',
      addOperation: { kind: 'async', status: 'registeringAlias' },
    });

    render(<AddModelWizard {...baseProps} providerPreselect="openai-chat" />);

    const { modelId, displayName } = catalogInputs();
    await user.type(modelId, 'reg-model');
    await user.type(displayName, 'Reg Model');
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Continue' }));
    await user.click(screen.getByRole('button', { name: 'Create model' }));

    await waitFor(() => expect(screen.getByText(/Registering alias/i)).toBeInTheDocument());
    onUpdate?.({ status: 'completed' });
    onTerminal?.({ status: 'completed' });
    await waitFor(() => expect(screen.getByText('Completed')).toBeInTheDocument());
  });
});
