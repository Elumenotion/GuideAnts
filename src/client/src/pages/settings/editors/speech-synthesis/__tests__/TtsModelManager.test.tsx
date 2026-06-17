import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { TtsModelManager } from '../TtsModelManager';

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
        modelId: 'hexgrad/Kokoro-82M',
        modelRef: 'Kokoro-82M',
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

describe('TtsModelManager', () => {
  beforeEach(() => {
    vi.resetAllMocks();
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

  it('opens add-model dialog and starts HF download only after browse resolves', async () => {
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
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: 'hexgrad/Kokoro-82M',
      status: 'queued',
      error: null,
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No TTS models installed/i)).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));

    const downloadButton = screen.getByRole('button', { name: /Download snapshot/i });
    expect(downloadButton).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));

    await waitFor(() => {
      expect(downloadButton).not.toBeDisabled();
    });

    fireEvent.click(downloadButton);

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('SpeechSynthesis', { model_id: 'hexgrad/Kokoro-82M' });
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
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      modelId: 'hexgrad/Kokoro-82M',
      modelRef: 'Kokoro-82M',
      status: 'queued',
      error: null,
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Add model/i)).toBeInTheDocument();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('SpeechSynthesis', { model_path: 'Kokoro-82M' });
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

  it('unloads the active model from the engine', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/tts',
          items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: true }],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/tts',
          items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: false }],
        },
      });
    (api.settings.localModels.runtimeReadinessOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: true, loaded: true, modelRef: '/models-local/tts/acme--tts', tokenizerRef: null },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
      });
    (api.settings.localModels.unload as any).mockResolvedValueOnce({ status: 'unloaded' });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Unload/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Unload/i }));

    await waitFor(() => {
      expect(api.settings.localModels.unload).toHaveBeenCalledWith('SpeechSynthesis');
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

  it('reports unload conflicts from the API', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/tts',
        items: [{ modelRef: 'acme--tts', isDirectory: true, activeModel: true }],
      },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: true, loaded: true, modelRef: '/models-local/tts/acme--tts', tokenizerRef: null },
    });
    (api.settings.localModels.unload as any).mockRejectedValueOnce({ status: 409 });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Unload/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Unload/i }));

    await waitFor(() => {
      expect(screen.getByText(/already in progress/i)).toBeInTheDocument();
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

  it('shows Kokoro defaults in the add-model dialog without tokenizer controls', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/tts', items: [] },
    });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null, tokenizerRef: null },
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));

    expect(screen.getByDisplayValue('hexgrad/Kokoro-82M')).toHaveAttribute('readonly');
    expect(screen.getByText(/Local TTS is fixed to Kokoro/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Tokenizer repository/i })).not.toBeInTheDocument();
  });

  it('flags voice artifacts in browse preview', async () => {
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
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'voices/en_US.npz', size: 10, category: 'other', quantLabel: null, sharded: false }],
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));

    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));

    await waitFor(() => {
      expect(screen.getByText('voice')).toBeInTheDocument();
    });
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
        modelId: 'hexgrad/Kokoro-82M',
        status: 'running',
        error: null,
      });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'hexgrad/Kokoro-82M',
      status: 'running',
      error: null,
    });
    (api.settings.localModels.cancelOperation as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      modelId: 'hexgrad/Kokoro-82M',
      status: 'cancelled',
      error: null,
    });

    render(<TtsModelManager enabled />);

    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
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
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce(new Error('queue full'));

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByText(/queue full/i)).toBeInTheDocument());
  });

  it('surfaces cancel failures while a download is in flight', async () => {
    mockAvailableList();
    mockAvailableReadiness();
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({ operationId: 'op-2', modelId: 'hexgrad/Kokoro-82M', status: 'running', error: null });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-2',
      modelId: 'hexgrad/Kokoro-82M',
      status: 'running',
      error: null,
    });
    (api.settings.localModels.cancelOperation as any).mockRejectedValueOnce(new Error('cancel denied'));

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
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
      onUpdate({ operationId: 'op-3', modelId: 'hexgrad/Kokoro-82M', status: 'running', error: null });
      onPollFailureThreshold?.();
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-3',
      modelId: 'hexgrad/Kokoro-82M',
      status: 'running',
      error: null,
    });

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getAllByText(/no longer reachable/i).length).toBeGreaterThan(0);
    });
  });

  it('downloads Kokoro snapshot with optional revision', async () => {
    mockAvailableList([], '/models-local/tts');
    mockAvailableReadiness();
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-tok',
      modelId: 'hexgrad/Kokoro-82M',
      status: 'queued',
      error: null,
    });
    mockStartPoll.mockImplementationOnce(({ onUpdate, onTerminal }) => {
      const terminal = { operationId: 'op-tok', modelId: 'hexgrad/Kokoro-82M', status: 'completed', error: null };
      onUpdate(terminal);
      onTerminal?.(terminal);
      return 1;
    });

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());

    fireEvent.change(screen.getByLabelText(/Revision \(optional\)/i), { target: { value: 'release' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('SpeechSynthesis', {
        model_id: 'hexgrad/Kokoro-82M',
        revision: 'release',
      });
    });
  });

  it('closes add-model dialog on cancel', async () => {
    mockAvailableList();
    mockAvailableReadiness();

    render(<TtsModelManager enabled />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Add model/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));

    fireEvent.click(screen.getAllByRole('button', { name: /^Cancel$/i })[0]!);
    await waitFor(() => {
      expect(screen.queryByText(/Add TTS model from Hugging Face/i)).not.toBeInTheDocument();
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
      onUpdate({ operationId: 'op-4', modelId: 'hexgrad/Kokoro-82M', status: 'running', error: null });
      return 1;
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'hexgrad/Kokoro-82M',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [{ path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false }],
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-4',
      modelId: 'hexgrad/Kokoro-82M',
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

    fireEvent.click(screen.getByRole('button', { name: /Add model/i }));
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));
    await waitFor(() => expect(screen.getByRole('button', { name: /Download snapshot/i })).not.toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(onDownloadOperationChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'SpeechSynthesis', operationId: 'op-4', inFlight: true })
      );
    });
  });

  it('renders loaded model and tokenizer badges and surfaces generic unload failures', async () => {
    mockAvailableList([
      { modelRef: 'acme--tts', isDirectory: false, activeModel: true, activeTokenizer: true },
    ]);
    mockAvailableReadiness({ ready: true, loaded: true, modelRef: '/models-local/tts/acme--tts', tokenizerRef: '/tok' });
    (api.settings.localModels.unload as any).mockRejectedValueOnce(new Error('unload exploded'));

    render(<TtsModelManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Loaded \(tokenizer\)/i)).toBeInTheDocument();
      expect(screen.getByText('file')).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: /^Remove$/i })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: /Unload/i }));
    await waitFor(() => expect(screen.getByText(/unload exploded/i)).toBeInTheDocument());
  });
});

