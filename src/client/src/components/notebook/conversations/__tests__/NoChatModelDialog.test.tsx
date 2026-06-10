import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import { NoChatModelDialog } from '../NoChatModelDialog';

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

describe('NoChatModelDialog', () => {
  const onClose = vi.fn();
  const onGoToSettings = vi.fn();

  it('renders nothing when closed', () => {
    render(
      <NoChatModelDialog isOpen={false} onClose={onClose} onGoToSettings={onGoToSettings} />
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('shows title and guidance when open', () => {
    render(
      <NoChatModelDialog isOpen onClose={onClose} onGoToSettings={onGoToSettings} />
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('No Chat Model Configured')).toBeInTheDocument();
    expect(screen.getByText(/configure a default chat model/i)).toBeInTheDocument();
  });

  it('lists blockers when provided', () => {
    render(
      <NoChatModelDialog
        isOpen
        onClose={onClose}
        onGoToSettings={onGoToSettings}
        blockers={['No API key configured', 'Provider unreachable']}
      />
    );

    expect(screen.getByText('No API key configured')).toBeInTheDocument();
    expect(screen.getByText('Provider unreachable')).toBeInTheDocument();
  });

  it('calls onClose when Dismiss is clicked', async () => {
    const user = userEvent.setup();
    render(
      <NoChatModelDialog isOpen onClose={onClose} onGoToSettings={onGoToSettings} />
    );

    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('calls onGoToSettings when Open Settings is clicked', async () => {
    const user = userEvent.setup();
    render(
      <NoChatModelDialog isOpen onClose={onClose} onGoToSettings={onGoToSettings} />
    );

    await user.click(screen.getByRole('button', { name: 'Open Settings' }));
    expect(onGoToSettings).toHaveBeenCalledTimes(1);
  });
});
