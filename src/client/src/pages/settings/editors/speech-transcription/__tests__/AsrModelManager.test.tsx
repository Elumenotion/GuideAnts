import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { AsrModelManager } from '../AsrModelManager';

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
        modelId: 'acme/asr',
        modelRef: 'acme--asr',
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
    { id: 'qwen3_asr_0_6b', displayName: 'Qwen3-ASR-0.6B', default: true },
  ]
) {
  (api.settings.localModels.catalogOutcome as any).mockResolvedValue({
    kind: 'available',
    payload: { version: 1, entries },
  });
}

function mockAvailableList(items: unknown[] = [], modelDir = '/models-local/asr') {
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

const DEFAULT_CATALOG_MODEL_ID = 'qwen3_asr_0_6b';

async function openCatalogDownloadDialog() {
  fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
  await waitFor(() => {
    expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled();
  });
}

describe('AsrModelManager', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mockAvailableCatalog();
  });

  afterEach(() => {
    cleanup();
  });

  it('renders nothing when disabled', () => {
    const { container } = render(<AsrModelManager enabled={false} />);
    expect(container).toBeEmptyDOMElement();
    expect(api.settings.localModels.listOutcome).not.toHaveBeenCalled();
    expect(api.settings.localModels.runtimeReadinessOutcome).not.toHaveBeenCalled();
  });

  it('loads a selected installed model by model_path', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/asr',
          items: [{ modelRef: 'acme--asr', isDirectory: true, sizeBytes: 0, active: false }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/asr',
          items: [{ modelRef: 'acme--asr', isDirectory: true, sizeBytes: 0, active: true }],
        },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: true, loaded: true, modelRef: '/models-local/asr/acme--asr' },
      });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No model loaded/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /^Load$/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('SpeechTranscription', { model_path: 'acme--asr' });
    });
    await waitFor(() => {
      expect(screen.getByText('Ready')).toBeInTheDocument();
    });
  });

  it('opens add-model dialog and starts catalog download immediately', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/asr', items: [] },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/asr', items: [] },
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

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No ASR models installed/i)).toBeInTheDocument();
    });
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('SpeechTranscription', {
        model_id: DEFAULT_CATALOG_MODEL_ID,
      });
    });
  });

  it('auto-loads downloaded model using operation modelRef contract', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/asr',
        items: [{ modelRef: 'acme--asr', isDirectory: true, sizeBytes: 0, active: false }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: DEFAULT_CATALOG_MODEL_ID,
      modelRef: 'acme--asr',
      status: 'queued',
      error: null,
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Add model/i)).toBeInTheDocument();
    });
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('SpeechTranscription', { model_path: 'acme--asr' });
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

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/probe blew up/i)).toBeInTheDocument();
    });
  });

  it('removes an installed model after confirmation', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/asr',
          items: [{ modelRef: 'acme--asr', isDirectory: true, sizeBytes: 0, active: false }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/asr', items: [] },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.remove as any).mockResolvedValueOnce({ status: 'removed' });

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /^Remove$/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /^Remove$/i }));
    fireEvent.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.localModels.remove).toHaveBeenCalledWith('SpeechTranscription', 'acme--asr');
    });
  });

  it('surfaces load failures in the action error banner', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/asr',
        items: [{ modelRef: 'acme--asr', isDirectory: true, sizeBytes: 0, active: false }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.load as any).mockRejectedValueOnce(new Error('load failed'));

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /^Load$/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /^Load$/i }));

    await waitFor(() => {
      expect(screen.getByText(/load failed/i)).toBeInTheDocument();
    });
  });

  it('notifies parent callbacks about runtime readiness', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/asr', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: true, loaded: true, modelRef: '/models-local/asr/acme--asr' },
    });

    render(<AsrModelManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'SpeechTranscription', ready: true, status: 'Ready' })
      );
    });
  });

  it('reports runtime readiness probe unavailable', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/asr', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'error',
      message: 'readiness offline',
    });

    render(<AsrModelManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(screen.getByText(/Runtime readiness probe not available/i)).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenLastCalledWith(
        expect.objectContaining({
          serviceId: 'SpeechTranscription',
          ready: false,
          status: 'Runtime readiness probe unavailable',
        })
      );
    });
  });

  it('surfaces remove failures', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/asr',
        items: [{ modelRef: 'acme--asr', isDirectory: true, sizeBytes: 0, active: false }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.remove as any).mockRejectedValueOnce(new Error('remove blocked'));

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /^Remove$/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /^Remove$/i }));
    fireEvent.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(screen.getByText(/remove blocked/i)).toBeInTheDocument();
    });
  });

  it('cancels an in-flight download and surfaces cancel failures', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-cancel', modelId: 'acme/asr', status: 'running', error: null });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/asr',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'acme/asr',
      status: 'running',
      error: null,
    });
    (api.settings.localModels.cancelOperation as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'acme/asr',
      status: 'cancelled',
      error: null,
    });

    render(<AsrModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Cancel$/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/i }));
    await waitFor(() => {
      expect(api.settings.localModels.cancelOperation).toHaveBeenCalledWith('SpeechTranscription', 'op-cancel');
    });

    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-2', modelId: 'acme/asr', status: 'running', error: null });
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-2',
      modelId: 'acme/asr',
      status: 'running',
      error: null,
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/asr',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.cancelOperation as any).mockRejectedValueOnce(new Error('cancel denied'));
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /^Cancel$/i })).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: /^Cancel$/i }));
    await waitFor(() => expect(screen.getByText(/cancel denied/i)).toBeInTheDocument());
  });

  it('surfaces catalog errors and download start failures in the add-model dialog', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    (api.settings.localModels.catalogOutcome as any).mockResolvedValueOnce({
      kind: 'error',
      message: 'catalog offline',
    });
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce(new Error('download refused'));

    render(<AsrModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    await waitFor(() => expect(screen.getByText(/catalog offline/i)).toBeInTheDocument());

    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    mockAvailableCatalog();
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByText(/download refused/i)).toBeInTheDocument());
  });

  it('includes revision when starting download and closes dialog on cancel', async () => {
    mockAvailableList();
    mockAvailableReadiness();
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

    render(<AsrModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.change(screen.getByLabelText(/Revision \(optional\)/i), { target: { value: 'v2' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('SpeechTranscription', {
        model_id: DEFAULT_CATALOG_MODEL_ID,
        revision: 'v2',
      });
    });

    await openCatalogDownloadDialog();
    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    await waitFor(() => {
      expect(screen.queryByText(/Download curated ASR model/i)).not.toBeInTheDocument();
    });
  });

  it('shows loading, warmup, and load-error engine details', async () => {
    mockAvailableList([{ modelRef: 'weights.bin', isDirectory: false, sizeBytes: 1536, active: false }]);
    mockAvailableReadiness({
      ready: false,
      loaded: false,
      loading: true,
      modelRef: null,
      warmupEnabled: true,
      warmupRan: true,
      warmupSucceeded: false,
      warmupError: 'warmup timeout',
      loadError: 'cuda missing',
    });

    render(<AsrModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Loading…/i)).toBeInTheDocument();
      expect(screen.getByText(/warmup timeout/i)).toBeInTheDocument();
      expect(screen.getByText(/Load error: cuda missing/i)).toBeInTheDocument();
      expect(screen.getByText(/1\.5 KB file/i)).toBeInTheDocument();
    });
  });

  it('surfaces poll unreachable errors during download', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate, onPollFailureThreshold }) => {
      onUpdate({ operationId: 'op-3', modelId: 'acme/asr', status: 'running', error: null });
      onPollFailureThreshold?.();
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/asr',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-3',
      modelId: 'acme/asr',
      modelRef: 'acme--asr',
      status: 'running',
      error: null,
    });

    render(<AsrModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => expect(screen.getAllByText(/no longer reachable/i).length).toBeGreaterThan(0));
  });

  it('surfaces auto-load failures after download completes', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate, onTerminal }) => {
      const terminal = {
        operationId: 'op-4',
        modelId: 'acme/asr',
        modelRef: 'acme--asr',
        status: 'completed',
        error: null,
      };
      onUpdate(terminal);
      onTerminal?.(terminal);
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/asr',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-4',
      modelId: 'acme/asr',
      modelRef: 'acme--asr',
      status: 'queued',
      error: null,
    });
    (api.settings.localModels.load as any).mockRejectedValueOnce(new Error('auto-load failed'));

    render(<AsrModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('SpeechTranscription', { model_path: 'acme--asr' });
    });
  });

  it('reports loading-model readiness and download callback state', async () => {
    const onRuntimeReadinessChange = vi.fn();
    const onDownloadOperationChange = vi.fn();
    mockAvailableList();
    mockAvailableReadiness({ ready: false, loaded: false, loading: true, modelRef: null });
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-5', modelId: 'acme/asr', status: 'running', error: 'disk full' });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/asr',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-5',
      modelId: 'acme/asr',
      status: 'running',
      error: null,
    });

    render(
      <AsrModelManager
        enabled
        onRuntimeReadinessChange={onRuntimeReadinessChange}
        onDownloadOperationChange={onDownloadOperationChange}
      />
    );

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'SpeechTranscription', ready: false, status: 'Loading model' })
      );
    });

    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    await openCatalogDownloadDialog();
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(onDownloadOperationChange).toHaveBeenCalledWith(
        expect.objectContaining({ operationId: 'op-5', inFlight: true, error: 'disk full' })
      );
    });
  });

});
