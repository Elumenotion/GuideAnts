import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { EmbRuntimeManager } from '../EmbRuntimeManager';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        listOutcome: vi.fn(),
        catalogOutcome: vi.fn(),
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

function mockAvailableCatalog(
  entries: Array<{ id: string; displayName?: string; producedDimension?: number; default?: boolean }> = [
    { id: 'qwen3_embedding_0_6b', displayName: 'Qwen3-Embedding-0.6B', producedDimension: 1024, default: true },
  ]
) {
  (api.settings.localModels.catalogOutcome as any).mockResolvedValue({
    kind: 'available',
    payload: { version: 1, entries },
  });
}

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

const DEFAULT_CATALOG_MODEL_ID = 'qwen3_embedding_0_6b';

async function openCatalogDownloadDialog() {
  fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
  await waitFor(() => {
    expect(screen.getByRole('button', { name: /Download GGUF/i })).not.toBeDisabled();
  });
}

describe('EmbRuntimeManager', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mockAvailableCatalog();
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

  it('opens add-model dialog and starts catalog download immediately', async () => {
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
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: DEFAULT_CATALOG_MODEL_ID,
      status: 'queued',
      error: null,
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No embedding models installed/i)).toBeInTheDocument();
    });
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('Embeddings', {
        model_id: DEFAULT_CATALOG_MODEL_ID,
      });
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
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: DEFAULT_CATALOG_MODEL_ID,
      modelRef: 'acme--emb',
      status: 'queued',
      error: null,
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<EmbRuntimeManager enabled onModelAutoLoaded={onModelAutoLoaded} />);

    await waitFor(() => {
      expect(screen.getByText(/Add model/i)).toBeInTheDocument();
    });
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));

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
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));
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
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Cancel$/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/i }));
    await waitFor(() => expect(screen.getByText(/cancel denied/i)).toBeInTheDocument());
  });

  it('surfaces catalog errors, download failures, and revision in add-model dialog', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    (api.settings.localModels.catalogOutcome as any).mockResolvedValueOnce({
      kind: 'error',
      message: 'catalog offline',
    });
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce(new Error('queue full'));

    render(<EmbRuntimeManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    await waitFor(() => expect(screen.getByText(/catalog offline/i)).toBeInTheDocument());

    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    mockAvailableCatalog();
    await openCatalogDownloadDialog();
    fireEvent.change(screen.getByLabelText(/Revision \(optional\)/i), { target: { value: 'main' } });
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));
    await waitFor(() => expect(screen.getByText(/queue full/i)).toBeInTheDocument());

    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-rev',
      modelId: DEFAULT_CATALOG_MODEL_ID,
      status: 'queued',
      error: null,
    });
    mockStartPoll.mockImplementationOnce(({ onUpdate, onTerminal }) => {
      const terminal = { operationId: 'op-rev', modelId: DEFAULT_CATALOG_MODEL_ID, status: 'completed', error: null };
      onUpdate(terminal);
      onTerminal?.(terminal);
      return 1;
    });
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));
    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('Embeddings', {
        model_id: DEFAULT_CATALOG_MODEL_ID,
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
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));
    await waitFor(() => expect(screen.getAllByText(/no longer reachable/i).length).toBeGreaterThan(0));
  });

  it('surfaces load and remove failures', async () => {
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
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    await waitFor(() => {
      expect(screen.queryByText(/Download curated embedding model/i)).not.toBeInTheDocument();
    });

    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download GGUF/i }));
    await waitFor(() => {
      expect(onDownloadOperationChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'Embeddings', operationId: 'op-4', inFlight: true })
      );
    });
  });

});
