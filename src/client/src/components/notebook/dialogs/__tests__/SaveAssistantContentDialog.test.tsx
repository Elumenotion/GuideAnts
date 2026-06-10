import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { render } from '@/test/test-utils';

import { SaveAssistantContentDialog } from '../SaveAssistantContentDialog';

const mockShowToast = vi.fn();

vi.mock('../../../common/Toast', async () => {
  const actual = await vi.importActual<typeof import('../../../common/Toast')>('../../../common/Toast');
  return {
    ...actual,
    useToast: () => ({ showToast: mockShowToast }),
  };
});

vi.mock('../../../../contexts/NotebookContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../contexts/NotebookContext')>();
  return {
    ...actual,
    useNotebook: () => ({
      projectId: 'proj-1',
      notebookId: 'nb-1',
    }),
  };
});

vi.mock('../../../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({
    folderTree: {
      name: 'root',
      relativePath: '',
      subFolders: [
        {
          name: 'Notes',
          relativePath: 'Notes',
          subFolders: [
            {
              name: 'Archive',
              relativePath: 'Notes/Archive',
              subFolders: [],
              files: [],
            },
          ],
          files: [],
        },
      ],
      files: [],
    },
  }),
}));

const sampleContent = 'This is a meaningful assistant response for testing save flow.';

const defaultProps = {
  isOpen: true,
  onClose: vi.fn(),
  onSave: vi.fn().mockResolvedValue(undefined),
  content: sampleContent,
  assistantName: 'Research Bot',
  notebookTitle: 'Field Notebook',
};

describe('SaveAssistantContentDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <SaveAssistantContentDialog {...defaultProps} isOpen={false} />
    );
    expect(container.querySelector('[aria-label="Close"]')).not.toBeInTheDocument();
  });

  it('renders dialog with content preview and generated filename', () => {
    render(<SaveAssistantContentDialog {...defaultProps} />);

    expect(screen.getByText('Save Assistant Response')).toBeInTheDocument();
    expect(screen.getByText(/to Field Notebook/)).toBeInTheDocument();
    expect(screen.getByText(/meaningful assistant response/i)).toBeInTheDocument();
    expect(screen.getByLabelText('File Name')).toHaveAttribute(
      'value',
      expect.stringMatching(/research-bot-.*\.md$/)
    );
    expect(screen.getByText(/From Research Bot/)).toBeInTheDocument();
  });

  it('shows validation error for invalid filename', async () => {
    const user = userEvent.setup();
    render(<SaveAssistantContentDialog {...defaultProps} />);

    const input = screen.getByLabelText('File Name');
    await user.clear(input);
    await user.type(input, 'invalid-name');
    await user.click(screen.getByRole('button', { name: 'Save File' }));

    expect(screen.getByText(/must end with \.md/)).toBeInTheDocument();
    expect(defaultProps.onSave).not.toHaveBeenCalled();
  });

  it('saves file to selected folder and shows success toast', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();

    render(
      <SaveAssistantContentDialog
        {...defaultProps}
        onSave={onSave}
        onClose={onClose}
      />
    );

    await user.click(screen.getByText('Notes'));
    await user.click(screen.getByRole('button', { name: 'Save File' }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.stringMatching(/\.md$/), 'Notes');
      expect(onClose).toHaveBeenCalled();
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'success',
          title: 'Assistant response saved',
        })
      );
    });
  });

  it('shows authentication error toast when save fails', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn().mockRejectedValue(new Error('Authentication expired'));

    render(<SaveAssistantContentDialog {...defaultProps} onSave={onSave} />);

    await user.click(screen.getByRole('button', { name: 'Save File' }));

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'error',
          title: 'Save Failed',
          message: 'Authentication expired. Please refresh and try again.',
        })
      );
    });
  });

  it('closes on Escape when not saving', async () => {
    const onClose = vi.fn();
    render(<SaveAssistantContentDialog {...defaultProps} onClose={onClose} />);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it('saves on Enter key', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    render(<SaveAssistantContentDialog {...defaultProps} onSave={onSave} />);

    fireEvent.keyDown(window, { key: 'Enter' });

    await waitFor(() => {
      expect(onSave).toHaveBeenCalled();
    });
  });

  it('shows network error toast when save fails with network message', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn().mockRejectedValue(new Error('Network unreachable'));

    render(<SaveAssistantContentDialog {...defaultProps} onSave={onSave} />);

    await user.click(screen.getByRole('button', { name: 'Save File' }));

    await waitFor(() => {
      expect(mockShowToast).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'error',
          message: 'Network error. Please check your connection and try again.',
        })
      );
    });
  });

  it('saves to root by default when no folder is selected', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(<SaveAssistantContentDialog {...defaultProps} onSave={onSave} />);

    await user.click(screen.getByRole('button', { name: 'Save File' }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.stringMatching(/\.md$/), undefined);
    });
  });

  it('can switch back to root after selecting a subfolder', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn().mockResolvedValue(undefined);

    render(<SaveAssistantContentDialog {...defaultProps} onSave={onSave} />);

    await user.click(screen.getByText('Notes'));
    await user.click(screen.getByText('Root'));
    await user.click(screen.getByRole('button', { name: 'Save File' }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(expect.stringMatching(/\.md$/), undefined);
    });
  });

  it('closes when close button is clicked', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<SaveAssistantContentDialog {...defaultProps} onClose={onClose} />);

    await user.click(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalled();
  });

  it('disables save button when disabled prop is true', () => {
    render(<SaveAssistantContentDialog {...defaultProps} disabled />);
    expect(screen.getByRole('button', { name: 'Save File' })).toBeDisabled();
  });
});
