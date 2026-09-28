import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { ToastProvider } from '../../../common/Toast';
import CompactButton from '../CompactButton';

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          compact: vi.fn(),
        },
      },
    },
  },
}));

import { api } from '../../../../services/api';

const renderButton = (props: Partial<React.ComponentProps<typeof CompactButton>> = {}) =>
  render(
    <CompactButton
      projectId="p1"
      notebookId="n1"
      conversationId="c1"
      {...props}
    />,
    { wrapper: ToastProvider },
  );

describe('CompactButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('is disabled when disabled=true and never calls the endpoint on click', async () => {
    renderButton({ disabled: true });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    expect(api.projects.notebooks.conversations.compact).not.toHaveBeenCalled();
  });

  it('calls the compact endpoint and shows forward-looking summarized-count feedback on a genuine success', async () => {
    const result = {
      boundaryTurnIndex: 5,
      messagesSummarized: 12,
      estimatedTokensBefore: 5000,
      estimatedTokensAfter: 900,
    };
    vi.mocked(api.projects.notebooks.conversations.compact).mockResolvedValue(result);
    const onSuccess = vi.fn();
    const onCompacted = vi.fn();
    renderButton({ onSuccess, onCompacted });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(api.projects.notebooks.conversations.compact).toHaveBeenCalledWith('p1', 'n1', 'c1');
      expect(screen.getByText(/conversation compacted/i)).toBeInTheDocument();
      expect(screen.getByText(/summarized 12 earlier message/i)).toBeInTheDocument();
      expect(screen.getByText(/next message you send/i)).toBeInTheDocument();
      expect(onSuccess).toHaveBeenCalled();
      expect(onCompacted).toHaveBeenCalledWith(result);
    });
  });

  it('shows an honest "nothing to compact" message and skips onSuccess/onCompacted on a no-op compaction', async () => {
    vi.mocked(api.projects.notebooks.conversations.compact).mockResolvedValue({
      boundaryTurnIndex: null,
      messagesSummarized: 0,
      estimatedTokensBefore: null,
      estimatedTokensAfter: null,
    });
    const onSuccess = vi.fn();
    const onCompacted = vi.fn();
    renderButton({ onSuccess, onCompacted });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(screen.getByText(/nothing to compact yet/i)).toBeInTheDocument();
    });
    expect(screen.queryByText(/conversation compacted/i)).not.toBeInTheDocument();
    expect(onSuccess).not.toHaveBeenCalled();
    expect(onCompacted).not.toHaveBeenCalled();
  });

  it('shows a busy message on 409 without calling onSuccess', async () => {
    const conflict: any = new Error('Conversation is locked by someone-else');
    conflict.status = 409;
    vi.mocked(api.projects.notebooks.conversations.compact).mockRejectedValue(conflict);
    const onSuccess = vi.fn();
    renderButton({ onSuccess });
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(screen.getByText(/conversation is busy/i)).toBeInTheDocument();
    });
    expect(onSuccess).not.toHaveBeenCalled();
  });

  it('shows a generic error message on any other failure', async () => {
    vi.mocked(api.projects.notebooks.conversations.compact).mockRejectedValue(new Error('boom'));
    renderButton();
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /compact/i }));

    await waitFor(() => {
      expect(screen.getByText(/failed to compact/i)).toBeInTheDocument();
    });
  });
});
