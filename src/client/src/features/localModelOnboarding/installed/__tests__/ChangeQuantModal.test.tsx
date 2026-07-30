import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ChangeQuantModal } from '../ChangeQuantModal';
import type { LlamaInstallationDetailDto } from '../../../../types/settings';

vi.mock('../../../../services/api', () => ({
  api: {
    settings: {
      getLlamaCatalogQuants: vi.fn(),
      changeLlamaInstallationQuant: vi.fn(),
    },
  },
}));

import { api } from '../../../../services/api';

const GIB = 1024 * 1024 * 1024;

function makeDetail(): LlamaInstallationDetailDto {
  return {
    modelId: 'llama/qwen',
    catalogId: 'qwen3.5-9b',
    catalogVersion: '2026.07.01',
    quantId: 'q4_k_m',
    quantLabel: 'Q4_K_M',
    routerModelId: 'Qwen3.5-9B-GGUF',
  } as never;
}

describe('ChangeQuantModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.settings.getLlamaCatalogQuants).mockResolvedValue({
      catalogId: 'qwen3.5-9b',
      repository: 'unsloth/Qwen3.5-9B-GGUF',
      requestedRevision: 'main',
      resolvedRevision: 'abc123def456',
      quants: [
        {
          id: 'q4_k_m',
          label: 'Q4_K_M',
          totalBytes: 16 * GIB,
          files: [{ path: 'Q4_K_M.gguf', size: 16 * GIB, shardCount: 1 }],
        },
        {
          id: 'q6_k',
          label: 'Q6_K',
          totalBytes: 21 * GIB,
          files: [{ path: 'Q6_K.gguf', size: 21 * GIB, shardCount: 1 }],
        },
      ],
      projector: null,
    } as never);
  });

  it('lists quants in a dropdown and blocks re-selecting the installed quant', async () => {
    render(
      <ChangeQuantModal isOpen detail={makeDetail()} onClose={vi.fn()} onOperationStarted={vi.fn()} />,
    );

    const select = await screen.findByLabelText('New quant group');
    expect(select).toBeInTheDocument();

    const installed = screen.getByRole('option', { name: /Q4_K_M .* installed/ });
    expect(installed).toBeDisabled();
    expect(screen.getByRole('option', { name: /Q6_K — 21 GiB · single file/ })).toBeEnabled();
  });

  it('names the installed quant without opening the dropdown', async () => {
    render(
      <ChangeQuantModal isOpen detail={makeDetail()} onClose={vi.fn()} onOperationStarted={vi.fn()} />,
    );

    const installedRow = (await screen.findByText('Installed quant')).nextElementSibling;
    expect(installedRow).toHaveTextContent('Q4_K_M');
    expect(installedRow).toHaveTextContent('16 GiB · single file');
  });

  it('reports when the installation has no recorded quant', async () => {
    render(
      <ChangeQuantModal
        isOpen
        detail={{ ...makeDetail(), quantId: null, quantLabel: null } as never}
        onClose={vi.fn()}
        onOperationStarted={vi.fn()}
      />,
    );

    expect(await screen.findByText('Not recorded for this installation')).toBeInTheDocument();
  });

  it('hands the started operation to the page tracker and closes', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const onOperationStarted = vi.fn();
    vi.mocked(api.settings.changeLlamaInstallationQuant).mockResolvedValue({
      operationId: 'op-9',
    } as never);

    render(
      <ChangeQuantModal
        isOpen
        detail={makeDetail()}
        onClose={onClose}
        onOperationStarted={onOperationStarted}
      />,
    );

    await user.selectOptions(await screen.findByLabelText('New quant group'), 'q6_k');
    await user.click(screen.getByRole('button', { name: 'Start change quant' }));

    await waitFor(() => {
      expect(api.settings.changeLlamaInstallationQuant).toHaveBeenCalledWith('llama/qwen', {
        quantId: 'q6_k',
        resolvedRevision: 'abc123def456',
      });
      expect(onOperationStarted).toHaveBeenCalledWith({
        operationId: 'op-9',
        kind: 'changeQuant',
        pollRoute: 'operations',
        routerModelId: 'Qwen3.5-9B-GGUF',
        catalogModelId: 'llama/qwen',
      });
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('surfaces a submit failure and stays open for another attempt', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    vi.mocked(api.settings.changeLlamaInstallationQuant).mockRejectedValue(new Error('Disk full'));

    render(
      <ChangeQuantModal isOpen detail={makeDetail()} onClose={onClose} onOperationStarted={vi.fn()} />,
    );

    await user.selectOptions(await screen.findByLabelText('New quant group'), 'q6_k');
    await user.click(screen.getByRole('button', { name: 'Start change quant' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Disk full');
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeEnabled();
  });

  it('renders a problem-details rejection as readable text', async () => {
    const user = userEvent.setup();
    const conflict: Error & { status?: number; body?: unknown } = new Error('Change quant failed');
    conflict.status = 409;
    conflict.body = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
      title: 'Change quant failed',
      status: 409,
      detail: "An operation is already in progress for alias 'Qwen3.5-9B-GGUF'.",
      code: 'OPERATION_IN_FLIGHT',
      remediation: 'Wait for the in-flight operation to complete, then retry.',
    };
    vi.mocked(api.settings.changeLlamaInstallationQuant).mockRejectedValue(conflict);

    render(
      <ChangeQuantModal isOpen detail={makeDetail()} onClose={vi.fn()} onOperationStarted={vi.fn()} />,
    );

    await user.selectOptions(await screen.findByLabelText('New quant group'), 'q6_k');
    await user.click(screen.getByRole('button', { name: 'Start change quant' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Change quant failed');
    expect(alert).toHaveTextContent("An operation is already in progress for alias 'Qwen3.5-9B-GGUF'.");
    expect(alert).toHaveTextContent('Wait for the in-flight operation to complete, then retry.');
    expect(alert).toHaveTextContent('OPERATION_IN_FLIGHT');
    expect(alert.textContent).not.toContain('rfc9110');
  });
});
