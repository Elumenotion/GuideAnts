import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup, within } from '@testing-library/react';
import { ImageBundleManager } from '../ImageBundleManager';

vi.mock('../../common', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../common')>();
  return {
    ...actual,
    RepositoryFilePicker: ({
      onRepositoryChange,
      onChange,
      serviceOrigin,
      repoInputLabel,
    }: {
      onRepositoryChange: (value: string) => void;
      onChange: (values: Record<string, string>) => void;
      serviceOrigin: string;
      repoInputLabel: string;
    }) => (
      <div data-testid={`picker-${serviceOrigin}`}>
        <span>{repoInputLabel}</span>
        <button
          type="button"
          onClick={() => {
            onRepositoryChange(`org/${serviceOrigin}`);
            const roleKey = serviceOrigin.includes('diffusion')
              ? 'diffusion'
              : serviceOrigin.includes('vae')
              ? 'vae'
              : 'textEncoder';
            onChange({ [roleKey]: `${roleKey}.bin` });
          }}
        >
          Pick {serviceOrigin}
        </button>
      </div>
    ),
  };
});

vi.mock('../../common/localOperationPolling', async () => {
  const actual = await vi.importActual<typeof import('../../common/localOperationPolling')>(
    '../../common/localOperationPolling'
  );
  return {
    ...actual,
    startLocalOperationPoll: vi.fn(() => 1),
  };
});

// Mock the api module so tests drive a known set of responses.
vi.mock('../../../../../services/api', () => ({
  api: {
    settings: {
      localModels: {
        listOutcome: vi.fn(),
        startDownload: vi.fn(),
        getOperation: vi.fn(),
        get: vi.fn(),
        selectActive: vi.fn(),
        remove: vi.fn(),
        cancelOperation: vi.fn(),
        load: vi.fn(),
      },
    },
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
import { api } from '../../../../../services/api';
import { startLocalOperationPoll } from '../../common/localOperationPolling';

const mockStartPoll = vi.mocked(startLocalOperationPoll);

describe('ImageBundleManager', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('renders the full upstream proxy failure (target URL, status, body) when the local models call fails', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'error',
      message: 'Upstream http://guideants-ai:80/sd/admin/bundles returned 404 Not Found.',
      upstream: {
        upstreamTarget: 'http://guideants-ai:80/sd/admin/bundles',
        upstreamStatus: 404,
        upstreamStatusText: 'Not Found',
        upstreamContentType: 'application/json',
        upstreamBody: '{"detail":"Not Found"}',
      },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/Upstream http:\/\/guideants-ai:80\/sd\/admin\/bundles returned 404/i)).toBeInTheDocument();
    });
    expect(screen.getByText('http://guideants-ai:80/sd/admin/bundles')).toBeInTheDocument();
    // "404 Not Found" appears both in the message paragraph and the Status <dd>;
    // both are legitimate, so assert at least one is present.
    expect(screen.getAllByText(/404 Not Found/).length).toBeGreaterThan(0);
    expect(screen.getByText('{"detail":"Not Found"}')).toBeInTheDocument();
    // Error phase has no product UI (no Download bundle button).
    expect(screen.queryByRole('button', { name: /Add bundle/i })).not.toBeInTheDocument();
  });

  it('renders each bundle with per-role status and disables activate for incomplete bundles', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'flux2-klein',
            active: false,
            complete: true,
            roles: {
              diffusion: { ready: true, path: '/models-local/sd/bundles/flux2-klein/diffusion' },
              vae: { ready: true, path: '/models-local/sd/bundles/flux2-klein/vae' },
              textEncoder: { ready: true, path: '/models-local/sd/bundles/flux2-klein/text_encoder' },
            },
          },
          {
            bundleId: 'broken-bundle',
            active: false,
            complete: false,
            roles: {
              diffusion: { ready: true },
              vae: { ready: false },
              textEncoder: { ready: true },
            },
          },
        ],
      },
    });

    render(<ImageBundleManager enabled />);

    await screen.findByText('flux2-klein');
    await screen.findByText('broken-bundle');

    // Both role cells and the status column say "Ready" for a complete bundle, so
    // there is at least one match; for the broken bundle the status is "Incomplete".
    expect(screen.getAllByText('Ready').length).toBeGreaterThan(0);
    expect(screen.getByText('Incomplete')).toBeInTheDocument();

    // The broken bundle's Activate button is disabled with an Incomplete-guard title.
    const rows = screen.getAllByRole('row');
    const brokenRow = rows.find((r) => r.textContent?.includes('broken-bundle'));
    expect(brokenRow).toBeTruthy();
    const activateButton = brokenRow!.querySelector('button[title*="Incomplete"]') as HTMLButtonElement | null;
    expect(activateButton).toBeTruthy();
    expect(activateButton!.disabled).toBe(true);
  });

  it('hot-swaps the loaded bundle without showing any restart-required banner', async () => {
    // Before activation: engine is loaded with bundle-a (old weights), bundle-b is
    // complete but not yet active.
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/sd',
          activeBundleId: 'bundle-a',
          loadedBundleId: 'bundle-a',
          engine: { processAlive: true, loadedBundleId: 'bundle-a', pid: 123 },
          items: [
            {
              bundleId: 'bundle-a',
              active: true,
              complete: true,
              loaded: true,
              roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
            },
            {
              bundleId: 'bundle-b',
              active: false,
              complete: true,
              loaded: false,
              roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
            },
          ],
        },
      })
      // After activation the component refreshes; the SD service hot-swapped, so
      // bundle-b is now both active and loaded.
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/sd',
          activeBundleId: 'bundle-b',
          loadedBundleId: 'bundle-b',
          engine: { processAlive: true, loadedBundleId: 'bundle-b', pid: 456 },
          items: [
            {
              bundleId: 'bundle-a',
              active: false,
              complete: true,
              loaded: false,
              roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
            },
            {
              bundleId: 'bundle-b',
              active: true,
              complete: true,
              loaded: true,
              roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
            },
          ],
        },
      });
    (api.settings.localModels.selectActive as any).mockResolvedValueOnce({
      ok: true,
      action: 'hot-swapped',
      activeBundleId: 'bundle-b',
    });

    render(<ImageBundleManager enabled />);

    // bundle-b's "Activate bundle" button is the one enabled initially.
    await screen.findByText('bundle-b');
    const rowsBefore = screen.getAllByRole('row');
    const bundleBRowBefore = rowsBefore.find((r) => r.textContent?.includes('bundle-b'));
    expect(bundleBRowBefore).toBeTruthy();
    const setActiveButton = bundleBRowBefore!.querySelector('button[title*="hot-swap"]') as HTMLButtonElement | null;
    expect(setActiveButton).toBeTruthy();
    fireEvent.click(setActiveButton!);

    await waitFor(() => {
      expect(api.settings.localModels.selectActive).toHaveBeenCalledWith('ImageGeneration', 'bundle-b');
    });
    // After the refresh, the UI must reflect the new loaded bundle and NEVER
    // show the obsolete "restart required" banner text.
    await waitFor(() => {
      const rows = screen.getAllByRole('row');
      const bundleBRowAfter = rows.find((r) => r.textContent?.includes('bundle-b'));
      expect(bundleBRowAfter?.textContent).toMatch(/Loaded/);
    });
    expect(screen.queryByText(/restart the/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Active bundle changed to/i)).not.toBeInTheDocument();
  });

  it('submits a download request with three (repo, file) pairs and surfaces the initial operation status', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      bundleId: 'my-bundle',
      status: 'queued',
      roles: { diffusion: 'queued', vae: 'queued', textEncoder: 'queued' },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    // Browse-first picker is now the default create flow; tests that drive
    // the legacy free-text inputs opt into advanced mode explicitly.
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));

    const bundleIdInput = await screen.findByLabelText(/Bundle id/i);
    fireEvent.change(bundleIdInput, { target: { value: 'my-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });

    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('ImageGeneration', {
        bundle_id: 'my-bundle',
        diffusion_repo: 'org/diffusion',
        diffusion_file: 'diff.gguf',
        vae_repo: 'org/vae',
        vae_file: 'vae.safetensors',
        text_encoder_repo: 'org/text-enc',
        text_encoder_file: 'enc.gguf',
      });
    });

    await waitFor(() => {
      expect(screen.getByText(/Download "my-bundle":\s*queued/i)).toBeInTheDocument();
    });
  });

  it('blocks submission when a required file field is empty', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    const bundleIdInput = await screen.findByLabelText(/Bundle id/i);
    fireEvent.change(bundleIdInput, { target: { value: 'my-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    // Deliberately leave Diffusion file blank.
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });

    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByText(/Diffusion file is required/i)).toBeInTheDocument();
    });
    expect(api.settings.localModels.startDownload).not.toHaveBeenCalled();
  });

  it('rejects filenames containing glob metacharacters before calling the api', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    const bundleIdInput = await screen.findByLabelText(/Bundle id/i);
    fireEvent.change(bundleIdInput, { target: { value: 'my-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: '*.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });

    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByText(/Diffusion file must be a single filename/i)).toBeInTheDocument();
    });
    expect(api.settings.localModels.startDownload).not.toHaveBeenCalled();
  });

  it('shows row-level updating states while a bundle download is in flight', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: {
              diffusion: { ready: true },
              vae: { ready: true },
              textEncoder: { ready: true },
            },
          },
        ],
      },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-1',
      bundleId: 'bundle-a',
      status: 'queued',
      roles: { diffusion: 'queued', vae: 'queued', textEncoder: 'queued' },
    });
    mockStartPoll.mockImplementationOnce(({ onUpdate }) => {
      onUpdate({
        operationId: 'op-1',
        bundleId: 'bundle-a',
        status: 'running',
        roles: { diffusion: 'downloading', vae: 'queued', textEncoder: 'queued' },
      });
      return 1;
    });

    render(<ImageBundleManager enabled />);

    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'bundle-a' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByText(/Updating/i)).toBeInTheDocument();
    });

    const rows = screen.getAllByRole('row');
    const bundleRow = rows.find((r) => r.textContent?.includes('bundle-a'));
    expect(bundleRow).toBeTruthy();
    expect(bundleRow!.textContent).toMatch(/Downloading|Queued/);
    expect(within(bundleRow!).getByRole('button', { name: /Activate bundle/i })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Add bundle/i })).toBeDisabled();
  });

  it('opens View for an installed bundle and shows read-only recipe fields', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: {
              diffusion: { ready: true, path: '/models-local/sd/bundles/bundle-a/diffusion' },
              vae: { ready: true, path: '/models-local/sd/bundles/bundle-a/vae' },
              textEncoder: { ready: true, path: '/models-local/sd/bundles/bundle-a/text-encoder' },
            },
          },
        ],
      },
    });
    (api.settings.localModels.get as any).mockResolvedValueOnce({
      bundleId: 'bundle-a',
      active: false,
      complete: true,
      loaded: false,
      definition: {
        revision: 'main',
        updatedAtUtc: '2026-04-20T00:00:00Z',
        roles: {
          diffusion: { repo: 'org/diffusion', file: 'diff.gguf' },
          vae: { repo: 'org/vae', file: 'vae.safetensors' },
          textEncoder: { repo: 'org/text', file: 'enc.gguf' },
        },
      },
      roles: {
        diffusion: { ready: true, path: '/models-local/sd/bundles/bundle-a/diffusion' },
        vae: { ready: true, path: '/models-local/sd/bundles/bundle-a/vae' },
        textEncoder: { ready: true, path: '/models-local/sd/bundles/bundle-a/text-encoder' },
      },
    });

    render(<ImageBundleManager enabled />);

    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /View details/i }));

    await waitFor(() => {
      expect(api.settings.localModels.get).toHaveBeenCalledWith('ImageGeneration', 'bundle-a');
    });

    const diffusionRepo = await screen.findByLabelText(/Diffusion repo/i);
    await waitFor(() => {
      expect((diffusionRepo as HTMLInputElement).value).toBe('org/diffusion');
    });
    expect((diffusionRepo as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText('Close')).toBeInTheDocument();
  });

  it('edits an installed bundle and re-downloads into the same bundle id', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: {
              diffusion: { ready: true },
              vae: { ready: true },
              textEncoder: { ready: true },
            },
          },
        ],
      },
    });
    (api.settings.localModels.get as any).mockResolvedValueOnce({
      bundleId: 'bundle-a',
      active: false,
      complete: true,
      loaded: false,
      definition: {
        revision: 'main',
        roles: {
          diffusion: { repo: 'org/diffusion', file: 'old-diff.gguf' },
          vae: { repo: 'org/vae', file: 'vae.safetensors' },
          textEncoder: { repo: 'org/text', file: 'enc.gguf' },
        },
      },
      roles: {
        diffusion: { ready: true },
        vae: { ready: true },
        textEncoder: { ready: true },
      },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-edit',
      bundleId: 'bundle-a',
      status: 'queued',
      roles: { diffusion: 'queued', vae: 'ready', textEncoder: 'ready' },
    });
    (api.settings.localModels.getOperation as any).mockResolvedValue({
      operationId: 'op-edit',
      bundleId: 'bundle-a',
      status: 'completed',
      roles: { diffusion: 'ready', vae: 'ready', textEncoder: 'ready' },
    });

    render(<ImageBundleManager enabled />);

    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /Edit bundle/i }));

    await waitFor(() => {
      expect(api.settings.localModels.get).toHaveBeenCalledWith('ImageGeneration', 'bundle-a');
    });

    const bundleIdInput = await screen.findByLabelText(/Bundle id/i);
    expect((bundleIdInput as HTMLInputElement).disabled).toBe(true);
    const diffusionFileInput = await screen.findByLabelText(/Diffusion file/i);
    await waitFor(() => {
      expect((diffusionFileInput as HTMLInputElement).value).toBe('old-diff.gguf');
    });
    fireEvent.change(diffusionFileInput, { target: { value: 'new-diff.gguf' } });

    const saveButton = screen.getByRole('button', { name: /Save & update/i });
    await waitFor(() => {
      expect((saveButton as HTMLButtonElement).disabled).toBe(false);
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('ImageGeneration', {
        bundle_id: 'bundle-a',
        diffusion_repo: 'org/diffusion',
        diffusion_file: 'new-diff.gguf',
        vae_repo: 'org/vae',
        vae_file: 'vae.safetensors',
        text_encoder_repo: 'org/text',
        text_encoder_file: 'enc.gguf',
        revision: 'main',
      });
    });
  });

  it('downloads a bundle definition JSON for an installed bundle', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: {
              diffusion: { ready: true },
              vae: { ready: true },
              textEncoder: { ready: true },
            },
          },
        ],
      },
    });
    (api.settings.localModels.get as any).mockResolvedValueOnce({
      bundleId: 'bundle-a',
      active: false,
      complete: true,
      loaded: false,
      definition: {
        revision: 'main',
        roles: {
          diffusion: { repo: 'org/diffusion', file: 'diff.gguf' },
          vae: { repo: 'org/vae', file: 'vae.safetensors' },
          textEncoder: { repo: 'org/text', file: 'enc.gguf' },
        },
      },
      roles: {
        diffusion: { ready: true },
        vae: { ready: true },
        textEncoder: { ready: true },
      },
    });

    if (!(URL as any).createObjectURL) {
      (URL as any).createObjectURL = () => 'blob:bundle-def';
    }
    if (!(URL as any).revokeObjectURL) {
      (URL as any).revokeObjectURL = () => {};
    }
    const createObjectUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:bundle-def');
    const revokeObjectUrlSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    const anchorClickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    render(<ImageBundleManager enabled />);

    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /Download definition/i }));

    await waitFor(() => {
      expect(api.settings.localModels.get).toHaveBeenCalledWith('ImageGeneration', 'bundle-a');
    });
    expect(createObjectUrlSpy).toHaveBeenCalledTimes(1);
    const blobArg = createObjectUrlSpy.mock.calls[0][0];
    expect(blobArg).toBeTruthy();
    expect(anchorClickSpy).toHaveBeenCalled();
    expect(revokeObjectUrlSpy).toHaveBeenCalledWith('blob:bundle-def');
    anchorClickSpy.mockRestore();
    createObjectUrlSpy.mockRestore();
    revokeObjectUrlSpy.mockRestore();
  });

  it('uploads a bundle definition JSON and prefills the download dialog', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    const definition = {
      schemaVersion: 1,
      bundleId: 'bundle-from-file',
      revision: 'refs/heads/release',
      roles: {
        diffusion: { repo: 'org/diffusion', file: 'diff.gguf' },
        vae: { repo: 'org/vae', file: 'vae.safetensors' },
        textEncoder: { repo: 'org/text-encoder', file: 'enc.gguf' },
      },
    };
    const uploadInput = screen.getByLabelText(/Upload bundle definition file/i) as HTMLInputElement;
    fireEvent.change(uploadInput, {
      target: {
        files: [new File([JSON.stringify(definition)], 'bundle-definition.json', { type: 'application/json' })],
      },
    });

    await waitFor(() => {
      expect((screen.getByLabelText(/Bundle id/i) as HTMLInputElement).value).toBe('bundle-from-file');
    });
    expect((screen.getByLabelText(/Diffusion repo/i) as HTMLInputElement).value).toBe('org/diffusion');
    expect((screen.getByLabelText(/Diffusion file/i) as HTMLInputElement).value).toBe('diff.gguf');
    expect((screen.getByLabelText(/VAE repo/i) as HTMLInputElement).value).toBe('org/vae');
    expect((screen.getByLabelText(/VAE file/i) as HTMLInputElement).value).toBe('vae.safetensors');
    expect((screen.getByLabelText(/Text encoder repo/i) as HTMLInputElement).value).toBe('org/text-encoder');
    expect((screen.getByLabelText(/Text encoder file/i) as HTMLInputElement).value).toBe('enc.gguf');
    expect((screen.getByLabelText(/Revision \(optional\)/i) as HTMLInputElement).value).toBe('refs/heads/release');
  });

  it('removes a bundle after confirmation', async () => {
    (api.settings.localModels.listOutcome as any)
      .mockResolvedValueOnce({
        kind: 'available',
        payload: {
          modelDir: '/models-local/sd',
          items: [
            {
              bundleId: 'bundle-remove',
              active: false,
              complete: true,
              loaded: false,
              roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
            },
          ],
        },
      })
      .mockResolvedValueOnce({
        kind: 'available',
        payload: { modelDir: '/models-local/sd', items: [] },
      });
    (api.settings.localModels.remove as any).mockResolvedValueOnce({ ok: true });

    render(<ImageBundleManager enabled />);

    await screen.findByText('bundle-remove');
    fireEvent.click(screen.getByRole('button', { name: /Remove bundle/i }));
    fireEvent.click(screen.getByTestId('confirm'));

    await waitFor(() => {
      expect(api.settings.localModels.remove).toHaveBeenCalledWith('ImageGeneration', 'bundle-remove');
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });
  });

  it('cancels an in-flight download operation', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      bundleId: 'bundle-a',
      status: 'running',
      roles: { diffusion: 'downloading', vae: 'queued', textEncoder: 'queued' },
    });
    (api.settings.localModels.cancelOperation as any).mockResolvedValueOnce({
      operationId: 'op-cancel',
      bundleId: 'bundle-a',
      status: 'cancelled',
    });

    render(<ImageBundleManager enabled />);

    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'bundle-a' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /Cancel/i })).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Cancel/i }));

    await waitFor(() => {
      expect(api.settings.localModels.cancelOperation).toHaveBeenCalledWith('ImageGeneration', 'op-cancel');
    });
  });

  it('shows last engine error in the status panel', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        activeBundleId: 'bundle-a',
        loadedBundleId: null,
        engine: { processAlive: false, lastError: 'CUDA init failed' },
        items: [
          {
            bundleId: 'bundle-a',
            active: true,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });

    render(<ImageBundleManager enabled />);

    expect(await screen.findByText(/Last engine error: CUDA init failed/i)).toBeInTheDocument();
  });

  it('includes optional revision when downloading from advanced form', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-rev',
      bundleId: 'rev-bundle',
      status: 'queued',
      roles: { diffusion: 'queued', vae: 'queued', textEncoder: 'queued' },
    });

    render(<ImageBundleManager enabled />);
    await waitFor(() => expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'rev-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.change(screen.getByLabelText(/Revision/i), { target: { value: 'main' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith(
        'ImageGeneration',
        expect.objectContaining({ revision: 'main' })
      );
    });
  });

  it('submits download using default repository pickers', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-picker',
      bundleId: 'picker-bundle',
      status: 'queued',
      roles: { diffusion: 'queued', vae: 'queued', textEncoder: 'queued' },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'picker-bundle' } });
    fireEvent.click(screen.getByText('Pick ImageGeneration.diffusion'));
    fireEvent.click(screen.getByText('Pick ImageGeneration.vae'));
    fireEvent.click(screen.getByText('Pick ImageGeneration.textEncoder'));
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(api.settings.localModels.startDownload).toHaveBeenCalledWith('ImageGeneration', {
        bundle_id: 'picker-bundle',
        diffusion_repo: 'org/ImageGeneration.diffusion',
        diffusion_file: 'diffusion.bin',
        vae_repo: 'org/ImageGeneration.vae',
        vae_file: 'vae.bin',
        text_encoder_repo: 'org/ImageGeneration.textEncoder',
        text_encoder_file: 'textEncoder.bin',
      });
    });
  });

  it('reports runtime readiness through callback props', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        activeBundleId: 'bundle-a',
        loadedBundleId: 'bundle-a',
        engine: { processAlive: true, loadedBundleId: 'bundle-a' },
        items: [
          {
            bundleId: 'bundle-a',
            active: true,
            complete: true,
            loaded: true,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });

    render(<ImageBundleManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({ serviceId: 'ImageGeneration', ready: true, status: 'Ready' })
      );
    });
  });

  it('reports not-ready readiness states for empty and misconfigured bundles', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        activeBundleId: null,
        loadedBundleId: null,
        engine: { processAlive: false },
        items: [],
      },
    });

    render(<ImageBundleManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({
          serviceId: 'ImageGeneration',
          ready: false,
          status: 'No bundles installed',
        })
      );
    });
  });

  it('rejects invalid and incomplete uploaded bundle definitions', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });

    render(<ImageBundleManager enabled />);
    await waitFor(() => expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument());

    const uploadInput = screen.getByLabelText(/Upload bundle definition file/i) as HTMLInputElement;
    fireEvent.change(uploadInput, {
      target: { files: [new File(['not-json'], 'bad.json', { type: 'application/json' })] },
    });
    await waitFor(() => {
      expect(screen.getByText(/not valid JSON/i)).toBeInTheDocument();
    });

    fireEvent.change(uploadInput, {
      target: {
        files: [new File([JSON.stringify({ bundleId: 'only-id' })], 'partial.json', { type: 'application/json' })],
      },
    });
    await waitFor(() => {
      expect(screen.getByText(/missing required field/i)).toBeInTheDocument();
    });
  });

  it('surfaces view and edit bundle detail load failures', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });
    (api.settings.localModels.get as any).mockRejectedValueOnce(new Error('details offline'));

    render(<ImageBundleManager enabled />);
    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /View details/i }));
    await waitFor(() => expect(screen.getByText(/details offline/i)).toBeInTheDocument());

    (api.settings.localModels.get as any).mockRejectedValueOnce(new Error('edit load failed'));
    fireEvent.click(screen.getByRole('button', { name: /Edit bundle/i }));
    await waitFor(() => expect(screen.getByText(/edit load failed/i)).toBeInTheDocument());
  });

  it('blocks definition export when bundle recipe is incomplete', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: false,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: false }, textEncoder: { ready: true } },
          },
        ],
      },
    });
    (api.settings.localModels.get as any).mockResolvedValueOnce({
      bundleId: 'bundle-a',
      active: false,
      complete: false,
      loaded: false,
      definition: { roles: { diffusion: { repo: 'org/d', file: 'd.gguf' } } },
      roles: { diffusion: { ready: true }, vae: { ready: false }, textEncoder: { ready: true } },
    });

    render(<ImageBundleManager enabled />);
    await screen.findByText('bundle-a');
    fireEvent.click(screen.getByRole('button', { name: /Download definition/i }));
    await waitFor(() => {
      expect(screen.getByText(/does not have a complete saved definition/i)).toBeInTheDocument();
    });
  });

  it('surfaces activate, remove, and cancel failures', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });
    (api.settings.localModels.selectActive as any).mockRejectedValueOnce(new Error('activate failed'));
    (api.settings.localModels.remove as any).mockRejectedValueOnce(new Error('remove blocked'));
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-fail',
      bundleId: 'bundle-a',
      status: 'running',
      roles: { diffusion: 'downloading', vae: 'queued', textEncoder: 'queued' },
    });
    (api.settings.localModels.cancelOperation as any).mockRejectedValueOnce(new Error('cancel denied'));

    render(<ImageBundleManager enabled />);
    await screen.findByText('bundle-a');

    fireEvent.click(screen.getByRole('button', { name: /Activate bundle/i }));
    await waitFor(() => expect(screen.getByText(/activate failed/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Remove bundle/i }));
    fireEvent.click(screen.getByTestId('confirm'));
    await waitFor(() => expect(screen.getByText(/remove blocked/i)).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'bundle-a' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getByTitle(/Cancel this bundle operation/i)).toBeInTheDocument());
    fireEvent.click(screen.getByTitle(/Cancel this bundle operation/i));
    await waitFor(() => expect(screen.getByText(/cancel denied/i)).toBeInTheDocument());
  });

  it('reports no-active-bundle readiness and poll unreachable errors', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        activeBundleId: null,
        loadedBundleId: null,
        engine: { processAlive: false },
        items: [
          {
            bundleId: 'bundle-a',
            active: false,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });

    render(<ImageBundleManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);
    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({ status: 'No active bundle selected' })
      );
    });

    mockStartPoll.mockImplementationOnce(({ onUpdate, onPollFailureThreshold }) => {
      onUpdate({
        operationId: 'op-unreach',
        bundleId: 'bundle-a',
        status: 'running',
        roles: { diffusion: 'downloading', vae: 'queued', textEncoder: 'queued' },
      });
      onPollFailureThreshold?.();
      return 1;
    });
    (api.settings.localModels.startDownload as any).mockResolvedValueOnce({
      operationId: 'op-unreach',
      bundleId: 'bundle-a',
      status: 'running',
      roles: { diffusion: 'downloading', vae: 'queued', textEncoder: 'queued' },
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'bundle-a' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));
    await waitFor(() => expect(screen.getAllByText(/no longer reachable/i).length).toBeGreaterThan(0));
  });

  it('reports engine-not-loaded readiness when an active bundle exists but engine is down', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        activeBundleId: 'bundle-a',
        loadedBundleId: null,
        engine: { processAlive: false },
        items: [
          {
            bundleId: 'bundle-a',
            active: true,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });

    render(<ImageBundleManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({
          serviceId: 'ImageGeneration',
          ready: false,
          status: 'Engine is not loaded',
        })
      );
    });
  });

  it('rejects glob metacharacters in filename fields', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'glob-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff*.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByText(/must be a single filename/i)).toBeInTheDocument();
    });
    expect(api.settings.localModels.startDownload).not.toHaveBeenCalled();
  });

  it('surfaces generic download dialog failures for non-Error throws', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce('network down');

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'fail-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByText('Download failed to start.')).toBeInTheDocument();
    });
  });

  it('surfaces download dialog submit failures from the api', async () => {
    (api.settings.localModels.listOutcome as any).mockResolvedValue({
      kind: 'available',
      payload: { modelDir: '/models-local/sd', items: [] },
    });
    (api.settings.localModels.startDownload as any).mockRejectedValueOnce(new Error('Upstream refused download'));

    render(<ImageBundleManager enabled />);

    await waitFor(() => {
      expect(screen.getByText(/No bundles installed/i)).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: /Add bundle/i }));
    fireEvent.click(await screen.findByRole('button', { name: /Advanced: free-text repo\/file/i }));
    fireEvent.change(await screen.findByLabelText(/Bundle id/i), { target: { value: 'fail-bundle' } });
    fireEvent.change(screen.getByLabelText(/Diffusion repo/i), { target: { value: 'org/diffusion' } });
    fireEvent.change(screen.getByLabelText(/Diffusion file/i), { target: { value: 'diff.gguf' } });
    fireEvent.change(screen.getByLabelText(/VAE repo/i), { target: { value: 'org/vae' } });
    fireEvent.change(screen.getByLabelText(/VAE file/i), { target: { value: 'vae.safetensors' } });
    fireEvent.change(screen.getByLabelText(/Text encoder repo/i), { target: { value: 'org/text-enc' } });
    fireEvent.change(screen.getByLabelText(/Text encoder file/i), { target: { value: 'enc.gguf' } });
    fireEvent.click(screen.getByRole('button', { name: /Download snapshot/i }));

    await waitFor(() => {
      expect(screen.getByText('Upstream refused download')).toBeInTheDocument();
    });
  });

  it('reports running-without-bundle readiness when engine is alive without a loaded bundle', async () => {
    const onRuntimeReadinessChange = vi.fn();
    (api.settings.localModels.listOutcome as any).mockResolvedValueOnce({
      kind: 'available',
      payload: {
        modelDir: '/models-local/sd',
        activeBundleId: 'bundle-a',
        loadedBundleId: null,
        engine: { processAlive: true, loadedBundleId: null },
        items: [
          {
            bundleId: 'bundle-a',
            active: true,
            complete: true,
            loaded: false,
            roles: { diffusion: { ready: true }, vae: { ready: true }, textEncoder: { ready: true } },
          },
        ],
      },
    });

    render(<ImageBundleManager enabled onRuntimeReadinessChange={onRuntimeReadinessChange} />);

    await waitFor(() => {
      expect(onRuntimeReadinessChange).toHaveBeenCalledWith(
        expect.objectContaining({
          serviceId: 'ImageGeneration',
          ready: false,
          status: 'Engine running without loaded bundle',
        })
      );
    });
  });
});


