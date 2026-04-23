import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import AssistantCell from '../AssistantCell';
import { ToastProvider } from '../../../common/Toast';

// Mock ChatMarkdownViewer and MessageDiff
vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>,
}));

vi.mock('../MessageDiff', () => ({
  __esModule: true,
  default: ({ original, edited }: { original: string; edited: string }) => (
    <div data-testid="message-diff">DIFF: {original} → {edited}</div>
  ),
}));

// Render portals inline
vi.mock('react-dom', () => ({
  ...vi.importActual('react-dom'),
  createPortal: (node: React.ReactNode) => node,
}));

const renderWithToast = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

describe('AssistantCell – edited message overlay', () => {
  const baseProps = {
    content: 'Edited content',
    originalContent: 'Original content',
    isLast: false,
    isEdited: true,
  } as const;

  it('renders "Edited" badge', () => {
    renderWithToast(<AssistantCell {...baseProps} />);
    const badges = screen.getAllByText(/^Edited$/i);
    expect(badges.length).toBeGreaterThan(0);
  });

  it('opens overlay with original content', async () => {
    const user = userEvent.setup();
    renderWithToast(<AssistantCell {...baseProps} />);

    await user.click(screen.getAllByLabelText('View original assistant response')[0]);
    expect(screen.getByTestId('markdown-viewer')).toHaveTextContent('Original content');
  });

  it('toggles diff view', async () => {
    const user = userEvent.setup();
    renderWithToast(<AssistantCell {...baseProps} />);
    await user.click(screen.getAllByLabelText('View original assistant response')[0]);

    await user.click(screen.getByRole('button', { name: /Show diff/i }));
    expect(screen.getByTestId('message-diff')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Hide diff/i }));
    expect(screen.queryByTestId('message-diff')).not.toBeInTheDocument();
  });

  it('closes overlay via close button', async () => {
    const user = userEvent.setup();
    renderWithToast(<AssistantCell {...baseProps} />);
    await user.click(screen.getAllByLabelText('View original assistant response')[0]);
    await user.click(screen.getByLabelText('Close original message view'));

    expect(screen.queryByLabelText('Close original message view')).not.toBeInTheDocument();
  });
}); 