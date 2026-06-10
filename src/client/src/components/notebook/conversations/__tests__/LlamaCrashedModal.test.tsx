import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import { LlamaCrashedModal } from '../LlamaCrashedModal';

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

describe('LlamaCrashedModal', () => {
  const baseProps = {
    isOpen: true,
    reason: 'Crashed' as const,
    onClose: vi.fn(),
    onRestart: vi.fn().mockResolvedValue(undefined),
    onAfterRestart: vi.fn(),
  };

  it('renders nothing when closed', () => {
    render(<LlamaCrashedModal {...baseProps} isOpen={false} />);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('shows OutOfMemory copy', () => {
    render(<LlamaCrashedModal {...baseProps} reason="OutOfMemory" />);
    expect(screen.getByText('Local model ran out of GPU memory')).toBeInTheDocument();
    expect(screen.getByText(/reducing the context size/i)).toBeInTheDocument();
  });

  it('shows default crashed copy for unknown reasons', () => {
    render(<LlamaCrashedModal {...baseProps} reason="UnknownError" />);
    expect(screen.getByText('Local model runtime crashed')).toBeInTheDocument();
  });

  it('shows upstream technical details when provided', async () => {
    const user = userEvent.setup();
    render(
      <LlamaCrashedModal
        {...baseProps}
        upstreamDetail="CUDA error: out of memory"
      />
    );

    await user.click(screen.getByText('Technical details'));
    expect(screen.getByText('CUDA error: out of memory')).toBeInTheDocument();
  });

  it('calls onClose when Dismiss is clicked', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<LlamaCrashedModal {...baseProps} onClose={onClose} />);

    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('restarts service and calls onAfterRestart on success', async () => {
    const user = userEvent.setup();
    const onRestart = vi.fn().mockResolvedValue(undefined);
    const onAfterRestart = vi.fn();

    render(
      <LlamaCrashedModal
        {...baseProps}
        onRestart={onRestart}
        onAfterRestart={onAfterRestart}
      />
    );

    await user.click(screen.getByRole('button', { name: 'Restart service' }));

    await waitFor(() => {
      expect(onRestart).toHaveBeenCalledTimes(1);
      expect(onAfterRestart).toHaveBeenCalledTimes(1);
    });
  });

  it('shows restart error and allows retry', async () => {
    const user = userEvent.setup();
    const onRestart = vi
      .fn()
      .mockRejectedValueOnce(new Error('Container not running'))
      .mockResolvedValueOnce(undefined);
    const onAfterRestart = vi.fn();

    render(
      <LlamaCrashedModal
        {...baseProps}
        onRestart={onRestart}
        onAfterRestart={onAfterRestart}
      />
    );

    await user.click(screen.getByRole('button', { name: 'Restart service' }));

    await waitFor(() => {
      expect(screen.getByText('Container not running')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Try again' }));

    await waitFor(() => {
      expect(onAfterRestart).toHaveBeenCalledTimes(1);
    });
  });

  it('disables actions while restarting', async () => {
    const user = userEvent.setup();
    let resolveRestart: () => void = () => {};
    const onRestart = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveRestart = resolve;
        })
    );

    render(<LlamaCrashedModal {...baseProps} onRestart={onRestart} />);

    await user.click(screen.getByRole('button', { name: 'Restart service' }));

    expect(screen.getByText('Restarting local model service…')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeDisabled();

    resolveRestart();
    await waitFor(() => {
      expect(screen.queryByText('Restarting local model service…')).not.toBeInTheDocument();
    });
  });
});
