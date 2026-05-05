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
});
