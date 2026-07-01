import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import { LlamaRuntimeModal } from '../LlamaRuntimeModal';

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

const baseStatus = {
  state: 'needs_load',
  requiredModels: [{ modelId: 'm1', displayName: 'Llama 3 8B' }],
  loadedModels: [{ modelId: 'm0', displayName: 'Old Model' }],
};

describe('LlamaRuntimeModal', () => {
  const onClose = vi.fn();
  const onStartLoad = vi.fn();

  it('renders nothing when closed or status is missing', () => {
    const { rerender } = render(
      <LlamaRuntimeModal isOpen={false} onClose={onClose} status={baseStatus} onStartLoad={onStartLoad} isPolling={false} />
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    rerender(
      <LlamaRuntimeModal isOpen onClose={onClose} status={null} onStartLoad={onStartLoad} isPolling={false} />
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('shows required and loaded models with load action', async () => {
    const user = userEvent.setup();
    render(
      <LlamaRuntimeModal
        isOpen
        onClose={onClose}
        status={baseStatus}
        onStartLoad={onStartLoad}
        isPolling={false}
      />
    );

    expect(screen.getByText('Local Models Required')).toBeInTheDocument();
    expect(screen.getByText('Llama 3 8B')).toBeInTheDocument();
    expect(screen.getByText('Old Model')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Load Models' }));
    expect(onStartLoad).toHaveBeenCalledTimes(1);
  });

  it('shows polling state with operation detail', () => {
    render(
      <LlamaRuntimeModal
        isOpen
        onClose={onClose}
        status={{
          ...baseStatus,
          activeOperation: { state: 'loading' },
        }}
        onStartLoad={onStartLoad}
        isPolling
      />
    );

    expect(screen.getByText('Loading Local Models...')).toBeInTheDocument();
    expect(screen.getByText('Loading new models into VRAM...')).toBeInTheDocument();
    expect(screen.queryByText('Loading your content...')).not.toBeInTheDocument();
    expect(screen.queryByText('Please wait while the required models are loaded into the local runtime.')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
  });

  it('shows external startup loading hint when polling external operation', () => {
    render(
      <LlamaRuntimeModal
        isOpen
        onClose={onClose}
        status={{
          ...baseStatus,
          activeOperation: { operationId: '__external_loading__', state: 'loading' },
        }}
        onStartLoad={onStartLoad}
        isPolling
      />
    );

    expect(
      screen.getByText('Models are already loading from startup or another session — no action needed.')
    ).toBeInTheDocument();
  });

  it('shows failed state with retry', async () => {
    const user = userEvent.setup();
    render(
      <LlamaRuntimeModal
        isOpen
        onClose={onClose}
        status={{
          state: 'failed',
          activeOperation: { errorDetails: 'Download timed out' },
        }}
        onStartLoad={onStartLoad}
        isPolling={false}
      />
    );

    expect(screen.getByText('Load Failed')).toBeInTheDocument();
    expect(screen.getByText('Download timed out')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Retry Load' }));
    expect(onStartLoad).toHaveBeenCalled();
  });

  it('shows invalid state with conflicts and no load button', () => {
    render(
      <LlamaRuntimeModal
        isOpen
        onClose={onClose}
        status={{
          state: 'invalid',
          conflicts: ['Model A conflicts with Model B'],
        }}
        onStartLoad={onStartLoad}
        isPolling={false}
      />
    );

    expect(screen.getByText('Incompatible Models')).toBeInTheDocument();
    expect(screen.getByText('Model A conflicts with Model B')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load Models' })).not.toBeInTheDocument();
  });

  it('calls onClose from Cancel', async () => {
    const user = userEvent.setup();
    render(
      <LlamaRuntimeModal
        isOpen
        onClose={onClose}
        status={baseStatus}
        onStartLoad={onStartLoad}
        isPolling={false}
      />
    );

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
