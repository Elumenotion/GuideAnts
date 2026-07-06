import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { TtsModelManager } from '../TtsModelManager';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        listOutcome: vi.fn(),
        catalogOutcome: vi.fn(),
        voicePackOutcome: vi.fn(),
        runtimeReadinessOutcome: vi.fn(),
        load: vi.fn(),
        startDownload: vi.fn(),
        getOperation: vi.fn(),
        cancelOperation: vi.fn(),
        remove: vi.fn(),
      },
      browseHuggingFaceRepository: vi.fn(),
    },
  },
}));

vi.mock('../../common/localOperationPolling', async () => {
  const actual = await vi.importActual<typeof import('../../common/localOperationPolling')>(
    '../../common/localOperationPolling'
  );
  return {
    ...actual,
    startLocalOperationPoll: vi.fn(({ onUpdate, onTerminal }) => {
      const terminal = {
        operationId: 'op-1',
        modelId: 'chatterbox',
        modelRef: 'chatterbox',
        status: 'completed',
        error: null,
      };
      onUpdate(terminal);
      onTerminal?.(terminal);
      return 1;
    }),
  };
});

// eslint-disable-next-line @typescript-eslint/no-var-requires
import { api } from '../../../../../services/api';
import { startLocalOperationPoll } from '../../common/localOperationPolling';

const mockStartPoll = vi.mocked(startLocalOperationPoll);

function mockAvailableCatalog(
  entries: Array<{ id: string; displayName?: string; default?: boolean }> = [
    { id: 'chatterbox', displayName: 'Chatterbox', default: true },
  ]
) {
  (api.settings.localModels.catalogOutcome as any).mockResolvedValue({
    kind: 'available',
    payload: { version: 1, entries },
  });
}

function mockAvailableList(items: unknown[] = [], modelDir = '/models-local/tts') {
  (api.settings.localModels.listOutcome as any).mockResolvedValue({
    kind: 'available',
    payload: { modelDir, items },
  });
}

function mockAvailableReadiness(payload: Record<string, unknown> = { ready: false, loaded: false, modelRef: null, tokenizerRef: null }) {
  (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
    kind: 'available',
    payload,
  });
}

const DEFAULT_CATALOG_MODEL_ID = 'chatterbox';

async function openCatalogDownloadDialog() {
  fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
  await waitFor(() => {
    expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled();
  });
}

describe('TtsModelManager', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mockAvailableCatalog();
  });

  afterEach(() => {
    cleanup();
  });

  it('renders nothing when disabled', () => {
    const { container } = render(<TtsModelManager enabled={false} />);
    expect(container).toBeEmptyDOMElement();
    expect(api.settings.localModels.listOutcome).not.toHaveBeenCalled();
    expect(api.settings.localModels.runtimeReadinessOutcome).not.toHaveBeenCalled();
  });

  it('loads a selected installed model by model_path', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/tts',
          items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: false }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/tts',
          items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: true }],
        },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          ready: true,
          loaded: true,
          modelRef: '/models-local/tts/acme--tts',
          tokenizerRef: '/models-local/tts/acme--tts',
        },
      });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No model loaded/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /^Load$/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('SpeechSynthesis', { model_path: 'acme--tts' });
    });
    await waitFor(() => {
      expect(screen.getByText('Ready')).toBeInTheDocument();
    });
  });

  it('opens add-model dialog and starts catalog download immediately', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/tts', items: [] },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/tts', items: [] },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
      });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: 'chatterbox',
      status: 'queued',
      error: null,
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No TTS models installed/i)).toBeInTheDocument();
    });
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('SpeechSynthesis', { model_id: 'chatterbox' });
    });
  });

  it('auto-loads downloaded model using operation modelRef contract', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/tts',
        items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: false }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: 'chatterbox',
      modelRef: 'chatterbox',
      status: 'queued',
      error: null,
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<TtsModelManager enabled />);

    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeInTheDocument());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('SpeechSynthesis', { model_path: 'chatterbox' });
    });
  });

  it('surfaces model-list probe failure', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'error',
      message: 'probe blew up',
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'error',
      message: 'probe blew up',
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/probe blew up/i)).toBeInTheDocument();
    });
  });

  it('removes an installed model after confirmation', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/tts',
          items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: false }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/tts', items: [] },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
    });
    (api.settings.localModels.remove as any).mockResolvedValueOnce({ status: 'removed' });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /^Remove$/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /^Remove$/i }));
    fireEvent.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.localModels.remove).toHaveBeenCalledWith('SpeechSynthesis', 'acme--tts');
    });
  });

  it('notifies parent callbacks about runtime readiness', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/tts', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: true, loaded: true, modelRef: '/models-local/tts/acme--tts', tokenizerRef: null },
    });

    render(<TtsModelManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'SpeechSynthesis', ready: true, status: 'Ready' })
      );
    });
  });

  it('shows chatterbox catalog defaults in the add-model dialog', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/tts', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
    });
    mockAvailableCatalog([{ id: 'chatterbox', displayName: 'Chatterbox (ResembleAI/chatterbox)', default: true }]);

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));

    await waitFor(() => {
      expect(screen.getByRole('option', { name: /Chatterbox/i })).toBeInTheDocument();
    });
    expect(screen.getByText(/curated catalog/i)).toBeInTheDocument();
    expect(api.settings.localModels.catalogOutcome).toHaveBeenCalledWith('SpeechSynthesis');
    expect(screen.queryByRole('button', { name: /Tokenizer repository/i })).not.toBeInTheDocument();
  });

  it('reports loaded-but-not-ready runtime status', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/tts', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: true, modelRef: '/models-local/tts/acme--tts', tokenizerRef: null },
    });

    render(<TtsModelManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenLastCalledWith(
        expect.objectContaining({
          serviceId: 'SpeechSynthesis',
          ready: false,
          status: 'Loaded, warmup pending',
        })
      );
    });
  });

  it('cancels an in-flight download operation', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({
        operationId: 'op-cancel',
        modelId: 'chatterbox',
        status: 'running',
        error: null,
      });
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'chatterbox',
      status: 'running',
      error: null,
    });
    (api.settings.localModels.cancelOperation as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'chatterbox',
      status: 'cancelled',
      error: null,
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => expect(screen.getByRole('button', { name: /^Cancel$/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/i }));

    await waitFor(() => {
      expect(api.settings.localModels.cancelOperation).toHaveBeenCalledWith('SpeechSynthesis', 'op-cancel');
    });
  });

  it('surfaces download start failures in the action banner', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce(new Error('queue full'));

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByText(/queue full/i)).toBeInTheDocument());
  });

  it('surfaces cancel failures while a download is in flight', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-2', modelId: 'chatterbox', status: 'running', error: null });
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-2',
      modelId: 'chatterbox',
      status: 'running',
      error: null,
    });
    (api.settings.localModels.cancelOperation as any).mockRejectedValueOnce(new Error('cancel denied'));

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByTitle(/Cancel this download operation/i)).toBeInTheDocument());
    fireEvent.click(screen.getByTitle(/Cancel this download operation/i));
    await waitFor(() => expect(screen.getByText(/cancel denied/i)).toBeInTheDocument());
  });

  it('surfaces load and remove failures', async () => {
    mockAvailableList([{ modelRef: 'acme--tts', isDirectory: true, activeModel: false }]);
    mockAvailableReadiness();
    (api.settings.localModels.load as any).mockRejectedValueOnce(new Error('load blew up'));
    (api.settings.localModels.remove as any).mockRejectedValueOnce(new Error('remove blocked'));

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /^Load$/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /^Load$/i }));
    await waitFor(() => expect(screen.getByText(/load blew up/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /^Remove$/i }));
    fireEvent.click(screen.getByTestId('confirm'));
    await waitFor(() => expect(screen.getByText(/remove blocked/i)).toBeInTheDocument());
  });

  it('surfaces poll unreachable errors and failed download status', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate, onPollFailureThreshold }) => {
      onUpdate({ operationId: 'op-3', modelId: 'chatterbox', status: 'running', error: null });
      onPollFailureThreshold?.();
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-3',
      modelId: 'chatterbox',
      status: 'running',
      error: null,
    });

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getAllByText(/no longer reachable/i).length).toBeGreaterThan(0);
    });
  });

  it('downloads chatterbox snapshot with optional revision', async () => {
    mockAvailableList([], '/models-local/tts');
    mockAvailableReadiness();
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-tok',
      modelId: 'chatterbox',
      status: 'queued',
      error: null,
    });
    mockStartPoll.mockImplementationOnce(({ onUpdate, onTerminal }) => {
      const terminal = { operationId: 'op-tok', modelId: 'chatterbox', status: 'completed', error: null };
      onUpdate(terminal);
      onTerminal?.(terminal);
      return 1;
    });

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();

    fireEvent.change(screen.getByLabelText(/Revision \(optional\)/i), { target: { value: 'release' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('SpeechSynthesis', {
        model_id: 'chatterbox',
        revision: 'release',
      });
    });
  });

  it('closes add-model dialog on cancel', async () => {
    mockAvailableList();
    mockAvailableReadiness();

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();

    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    await waitFor(() => {
      expect(screen.queryByText(/Download curated TTS model/i)).not.toBeInTheDocument();
    });
  });

  it('reports runtime readiness probe unavailable and notifies download callbacks', async () => {
    const onRuntimeReadinessChange = vi.fn();
    const onDownloadOperationChange = vi.fn();
    mockAvailableList();
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'error',
      message: 'readiness offline',
    });
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-4', modelId: 'chatterbox', status: 'running', error: null });
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-4',
      modelId: 'chatterbox',
      status: 'running',
      error: null,
    });

    render(
      <TtsModelManager
        enabled
        onRuntimeReadinessChange={onRuntimeReadinessChange}
        onDownloadOperationChange={onDownloadOperationChange}
      />
    );

    await waitFor(() => {
      expect(screen.getByText(/Runtime readiness probe not available/i)).toBeInTheDocument();
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({ status: 'Runtime readiness probe unavailable' })
      );
    });

    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(onDownloadOperationChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'SpeechSynthesis', operationId: 'op-4', inFlight: true })
      );
    });
  });

  it('renders loaded model and tokenizer badges', async () => {
    mockAvailableList([
      { modelRef: 'acme--tts', isDirectory: false, activeModel: true, activeTokenizer: true },
    ]);
    mockAvailableReadiness({ ready: true, loaded: true, modelRef: '/models-local/tts/acme--tts', tokenizerRef: '/tok' });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Loaded \(tokenizer\)/i)).toBeInTheDocument();
      expect(screen.getByText('file')).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: /^Remove$/i })).toBeDisabled();
  });
});

