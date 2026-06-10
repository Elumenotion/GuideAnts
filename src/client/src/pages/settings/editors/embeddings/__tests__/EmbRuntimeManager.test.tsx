import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { EmbRuntimeManager } from '../EmbRuntimeManager';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        listOutcome: vi.fn(),
        runtimeReadinessOutcome: vi.fn(),
        load: vi.fn(),
        unload: vi.fn(),
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
        modelId: 'acme/emb',
        modelRef: 'acme--emb',
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

function mockAvailableList(items: unknown[] = [], modelDir = '/models-local/emb') {
  (api.settings.localModels.listOutcome as any).mockResolvedValue({
    kind: 'available',
    payload: { modelDir, items },
  });
}

function mockAvailableReadiness(payload: Record<string, unknown> = { ready: false, loaded: false, modelRef: null }) {
  (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
    kind: 'available',
    payload,
  });
}

describe('EmbRuntimeManager', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('renders nothing when disabled', () => {
    const { container } = render(<EmbRuntimeManager enabled={false} />);
    expect(container).toBeEmptyDOMElement();
    expect(api.settings.localModels.listOutcome).not.toHaveBeenCalled();
    expect(api.settings.localModels.runtimeReadinessOutcome).not.toHaveBeenCalled();
  });

  it('loads a selected installed model by model_path', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/emb',
          items: [{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: false }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/emb',
          items: [{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: true }],
        },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: true, loaded: true, modelRef: '/models-local/emb/acme--emb' },
      });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No model loaded/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /^Load$/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('Embeddings', { model_path: 'acme--emb' });
    });
    await waitFor(() => {
      expect(screen.getByText('Ready')).toBeInTheDocument();
    });
  });

  it('opens add-model dialog and starts HF download only after browse resolves', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/emb', items: [] },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/emb', items: [] },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null },
      });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: 'acme/emb',
      status: 'queued',
      error: null,
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No embedding models installed/i)).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));

    const downloadButton = screen.getByRole('button', { name: /Download snapshot/i });
    expect(downloadButton).toBeDisabled();

    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));

    await waitFor(() => {
      expect(downloadButton).not.toBeDisabled();
    });

    fireEvent.click(downloadButton);

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('Embeddings', { model_id: 'acme/emb' });
    });
  });

  it('auto-loads downloaded model using operation modelRef contract', async () => {
    const onModelAutoLoaded = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/emb',
        items: [{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: false }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: 'acme/emb',
      modelRef: 'acme--emb',
      status: 'queued',
      error: null,
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<EmbRuntimeManager enabled onModelAutoLoaded={onModelAutoLoaded} />);

    await waitFor(() => {
      expect(screen.getByText(/Add model/i)).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('Embeddings', { model_path: 'acme--emb' });
    });
    await waitFor(() => {
      expect(onModelAutoLoaded).toHaveBeenCalledWith('acme--emb');
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

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/probe blew up/i)).toBeInTheDocument();
    });
  });

  it('unloads the active embedding model', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/emb',
          items: [{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: true }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/emb',
          items: [{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: false }],
        },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: true, loaded: true, modelRef: '/models-local/emb/acme--emb', warmupEnabled: true, warmupSucceeded: true },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null },
      });
    (api.settings.localModels.unload as any).mockResolvedValueOnce({ status: 'unloaded' });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Unload/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Unload/i }));

    await waitFor(() => {
      expect(api.settings.localModels.unload).toHaveBeenCalledWith('Embeddings');
    });
  });

  it('removes an installed model after confirmation', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/emb',
          items: [{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: false }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/emb', items: [] },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.remove as any).mockResolvedValueOnce({ status: 'removed' });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /^Remove$/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /^Remove$/i }));
    fireEvent.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.localModels.remove).toHaveBeenCalledWith('Embeddings', 'acme--emb');
    });
  });

  it('shows formatted file size for non-directory entries', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/emb',
        items: [{ modelRef: 'weights.bin', isDirectory: false, sizeBytes: 2048, active: false }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/2\.0 KB file/i)).toBeInTheDocument();
    });
  });

  it('handles unavailable model list responses', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce(null);
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Model list response was unavailable/i)).toBeInTheDocument();
    });
  });

  it('reports warmup-pending readiness to parent callbacks', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/emb', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        ready: false,
        loaded: true,
        loading: false,
        modelRef: '/models-local/emb/acme--emb',
        warmupEnabled: true,
        warmupRan: false,
        warmupSucceeded: false,
        warmupError: 'warmup failed',
      },
    });

    render(<EmbRuntimeManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({
          serviceId: 'Embeddings',
          ready: false,
          status: 'Loaded, warmup pending',
          detail: 'warmup failed',
        })
      );
    });
  });

  it('cancels in-flight downloads and surfaces cancel failures', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-cancel', modelId: 'acme/emb', status: 'running', error: null });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'acme/emb',
      status: 'running',
      error: null,
    });
    (api.settings.localModels.cancelOperation as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'acme/emb',
      status: 'cancelled',
      error: null,
    });

    render(<EmbRuntimeManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Cancel$/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/i }));
    await waitFor(() => {
      expect(api.settings.localModels.cancelOperation).toHaveBeenCalledWith('Embeddings', 'op-cancel');
    });

    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-2', modelId: 'acme/emb', status: 'running', error: null });
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-2',
      modelId: 'acme/emb',
      status: 'running',
      error: null,
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.cancelOperation as any).mockRejectedValueOnce(new Error('cancel denied'));
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Cancel$/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/i }));
    await waitFor(() => expect(screen.getByText(/cancel denied/i)).toBeInTheDocument());
  });

  it('surfaces browse errors, download failures, and revision in add-model dialog', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    (api.settings.browseHuggingFaceRepository as any).mockRejectedValueOnce(
      Object.assign(new Error('Repo missing.'), { code: 'NOT_FOUND', status: 404 })
    );
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce(new Error('queue full'));

    render(<EmbRuntimeManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByText(/Browse reported: Repo missing/i)).toBeInTheDocument());

    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.change(screen.getByPlaceholderText('main'), { target: { value: 'main' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByText(/queue full/i)).toBeInTheDocument());

    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-rev',
      modelId: 'acme/emb',
      status: 'queued',
      error: null,
    });
    mockStartPoll.mockImplementationOnce(({ onUpdate, onTerminal }) => {
      const terminal = { operationId: 'op-rev', modelId: 'acme/emb', status: 'completed', error: null };
      onUpdate(terminal);
      onTerminal?.(terminal);
      return 1;
    });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('Embeddings', {
        model_id: 'acme/emb',
        revision: 'main',
      });
    });
  });

  it('surfaces poll unreachable errors during download', async () => {
    mockAvailableList([{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: false }]);
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate, onPollFailureThreshold }) => {
      onUpdate({ operationId: 'op-3', modelId: 'acme/emb', status: 'running', error: null });
      onPollFailureThreshold?.();
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-3',
      modelId: 'acme/emb',
      status: 'running',
      error: null,
    });

    render(<EmbRuntimeManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getAllByText(/no longer reachable/i).length).toBeGreaterThan(0));
  });

  it('surfaces load, remove, and unload conflict failures', async () => {
    mockAvailableList([{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: false }]);
    mockAvailableReadiness();
    (api.settings.localModels.load as any).mockRejectedValueOnce(new Error('load failed'));

    render(<EmbRuntimeManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /^Load$/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /^Load$/i }));
    await waitFor(() => expect(screen.getByText(/load failed/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /^Remove$/i }));
    (api.settings.localModels.remove as any).mockRejectedValueOnce(new Error('remove blocked'));
    fireEvent.click(screen.getByTestId('confirm'));
    await waitFor(() => expect(screen.getByText(/remove blocked/i)).toBeInTheDocument());
  });

  it('surfaces unload conflicts from the API', async () => {
    mockAvailableList([{ modelRef: 'acme--emb', isDirectory: true, sizeBytes: 0, active: true }]);
    mockAvailableReadiness({ ready: true, loaded: true, modelRef: '/models-local/emb/acme--emb' });
    (api.settings.localModels.unload as any).mockRejectedValueOnce({ status: 409 });

    render(<EmbRuntimeManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Unload/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Unload/i }));
    await waitFor(() => expect(screen.getByText(/already in progress/i)).toBeInTheDocument());
  });

  it('shows engine device, dimensions, warmup failure, and load errors', async () => {
    mockAvailableList();
    mockAvailableReadiness({
      ready: false,
      loaded: true,
      loading: true,
      modelRef: '/models-local/emb/acme--emb',
      device: 'cuda:0',
      dimensions: 384,
      warmupEnabled: true,
      warmupRan: true,
      warmupSucceeded: false,
      warmupError: 'warmup failed',
      loadError: 'oom',
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Loading…/i)).toBeInTheDocument();
      expect(screen.getByText(/device cuda:0/i)).toBeInTheDocument();
      expect(screen.getByText(/384-d/i)).toBeInTheDocument();
      expect(screen.getByText(/warmup failed/i)).toBeInTheDocument();
      expect(screen.getByText(/Load error: oom/i)).toBeInTheDocument();
    });
  });

  it('closes add-model dialog on cancel and notifies download callbacks', async () => {
    const onDownloadOperationChange = vi.fn();
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-4', modelId: 'acme/emb', status: 'running', error: null });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-4',
      modelId: 'acme/emb',
      status: 'running',
      error: null,
    });

    render(<EmbRuntimeManager enabled onDownloadOperationChange={onDownloadOperationChange} />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    await waitFor(() => {
      expect(screen.queryByText(/Add embedding model from Hugging Face/i)).not.toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.change(screen.getByPlaceholderText(/microsoft\/harrier/i), { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => {
      expect(onDownloadOperationChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'Embeddings', operationId: 'op-4', inFlight: true })
      );
    });
  });

});
