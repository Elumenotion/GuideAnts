import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { EmbRuntimeManager } from '../EmbRuntimeManager';

vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        runtimeReadinessOutcome: vi.fn(),
        load: vi.fn(),
        unload: vi.fn(),
      },
      browseHuggingFaceRepository: vi.fn(),
    },
  },
}));

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
    expect(api.settings.localModels.runtimeReadinessOutcome).not.toHaveBeenCalled();
  });

  it('shows the engine state, enables Unload only when loaded, and forwards Unload to the API', async () => {
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        ready: true,
        loaded: true,
        modelRef: '/models-local/emb/harrier-oss-v1-0.6b',
        device: 'cuda',
        dimensions: 1024,
        warmupEnabled: true,
        warmupSucceeded: true,
      },
    });
    (api.settings.localModels.unload as any).mockResolvedValueOnce({ ok: true });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText('Ready')).toBeInTheDocument();
    });
    expect(screen.getByText(/harrier-oss-v1-0\.6b/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Load default model/i })).toBeDisabled();
    const unloadBtn = screen.getByRole('button', { name: 'Unload model' });
    expect(unloadBtn).not.toBeDisabled();

    fireEvent.click(unloadBtn);

    await waitFor(() => {
      expect(api.settings.localModels.unload).toHaveBeenCalledWith('Embeddings');
    });
    await waitFor(() => {
      expect(screen.getByText('Not loaded')).toBeInTheDocument();
    });
  });

  it('enables Load when no model is loaded and forwards Load to the API', async () => {
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        ready: true,
        loaded: true,
        modelRef: '/models-local/emb/default',
      },
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText('Not loaded')).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: 'Unload model' })).toBeDisabled();
    const loadBtn = screen.getByRole('button', { name: /Load default model/i });
    expect(loadBtn).not.toBeDisabled();

    fireEvent.click(loadBtn);

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('Embeddings', {});
    });
    await waitFor(() => {
      expect(screen.getByText('Ready')).toBeInTheDocument();
    });
  });

  it('blocks Load in HF mode until a successful browse has returned', async () => {
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.browseHuggingFaceRepository as any).mockResolvedValueOnce({
      repository: 'acme/emb',
      gated: false,
      tokenUsed: false,
      modelCardUrl: null,
      files: [
        { path: 'model.safetensors', size: 100, category: 'other', quantLabel: null, sharded: false },
      ],
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: true, loaded: true, modelRef: 'acme/emb' },
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText('Not loaded')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByLabelText(/From Hugging Face/i));
    const loadBtn = screen.getByRole('button', { name: /Load from Hugging Face/i });
    expect(loadBtn).toBeDisabled();

    const repoInput = screen.getByPlaceholderText(/owner\/repo/i) as HTMLInputElement;
    fireEvent.change(repoInput, { target: { value: 'acme/emb' } });
    fireEvent.click(screen.getByRole('button', { name: /Browse repository/i }));

    await waitFor(() => {
      expect(loadBtn).not.toBeDisabled();
    });

    fireEvent.click(loadBtn);

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('Embeddings', { model_id: 'acme/emb' });
    });
  });

  it('sends {model_path} when Local path mode is chosen', async () => {
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: false, loaded: false, modelRef: null },
    });
    (api.settings.localModels.load as any).mockResolvedValueOnce({ status: 'loaded' });
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: { ready: true, loaded: true, modelRef: '/models/local/emb' },
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText('Not loaded')).toBeInTheDocument();
    });
    fireEvent.click(screen.getByLabelText(/From local path/i));
    const pathInput = screen.getByLabelText(/Local model path/i) as HTMLInputElement;
    fireEvent.change(pathInput, { target: { value: '/models/local/emb' } });
    const loadBtn = screen.getByRole('button', { name: /Load from local path/i });
    expect(loadBtn).not.toBeDisabled();
    fireEvent.click(loadBtn);

    await waitFor(() => {
      expect(api.settings.localModels.load).toHaveBeenCalledWith('Embeddings', {
        model_path: '/models/local/emb',
      });
    });
  });

  it('surfaces a probe failure when runtime readiness is unavailable', async () => {
    (api.settings.localModels.runtimeReadinessOutcome as any).mockResolvedValueOnce({
      kind: 'error',
      message: 'probe blew up',
    });

    render(<EmbRuntimeManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Runtime readiness probe not available/i)).toBeInTheDocument();
    });
    expect(screen.getByText(/probe blew up/i)).toBeInTheDocument();
  });
});
