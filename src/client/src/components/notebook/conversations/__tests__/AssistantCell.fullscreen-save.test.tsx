import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import '@testing-library/jest-dom';
import AssistantCell from '../AssistantCell';
import { ToastProvider } from '../../../common/Toast';

vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>,
}));

vi.mock('../LexicalEditor', () => ({
  default: vi.fn().mockImplementation(({ ref, submitButton, cancelButton, onReady }) => {
    React.useEffect(() => {
      onReady?.();
    }, [onReady]);

    React.useImperativeHandle(ref, () => ({
      setValue: vi.fn(),
      getValue: vi.fn(() => 'Updated assistant text'),
      getIsEmpty: vi.fn(() => false),
      registerChangeListener: vi.fn(() => () => {}),
    }));

    return (
      <div data-testid="lexical-editor">
        {cancelButton && (
          <button type="button" onClick={cancelButton.onClick}>
            {cancelButton.label}
          </button>
        )}
        {submitButton && (
          <button type="button" onClick={submitButton.onClick} disabled={submitButton.disabled}>
            {submitButton.label}
          </button>
        )}
      </div>
    );
  }),
}));

vi.mock('../../dialogs/SaveAssistantContentDialog', () => ({
  SaveAssistantContentDialog: ({
    isOpen,
    onSave,
  }: {
    isOpen: boolean;
    onSave: (fileName: string, folderPath?: string) => Promise<void>;
  }) =>
    isOpen ? (
      <button type="button" onClick={() => onSave('saved-response.md', 'Output')}>
        Confirm save to notebook
      </button>
    ) : null,
}));

vi.mock('react-dom', async () => {
  const actual = await vi.importActual<typeof import('react-dom')>('react-dom');
  return {
    ...actual,
    createPortal: (node: React.ReactNode) => node,
  };
});

const renderWithToast = (component: React.ReactElement) =>
  render(<ToastProvider>{component}</ToastProvider>);

describe('AssistantCell – fullscreen and save paths', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('saves edited content from fullscreen editor and closes', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn().mockResolvedValue(undefined);

    renderWithToast(
      <AssistantCell
        content="Original reply"
        isLast
        canEdit
        onEditClick={vi.fn()}
        onSave={onSave}
      />,
    );

    await user.click(screen.getByLabelText('Edit message'));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith('Updated assistant text');
      expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
    });
  });

  it('cancels fullscreen edit and returns to inline view', async () => {
    const user = userEvent.setup();

    renderWithToast(
      <AssistantCell
        content="Editable reply"
        isLast
        canEdit
        onEditClick={vi.fn()}
        onSave={vi.fn()}
      />,
    );

    await user.click(screen.getByLabelText('Edit message'));
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.getByLabelText('Full screen')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument();
    });
  });

  it('exits fullscreen viewer without entering edit mode', async () => {
    const user = userEvent.setup();

    renderWithToast(
      <AssistantCell content="Read-only fullscreen" isLast={false} />,
    );

    await user.click(screen.getByLabelText('Full screen'));
    await waitFor(() => {
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText('Exit full screen'));

    await waitFor(() => {
      expect(screen.getByLabelText('Full screen')).toBeInTheDocument();
    });
  });

  it('saves assistant content to notebook via dialog', async () => {
    const user = userEvent.setup();
    const onSaveToNotebook = vi.fn().mockResolvedValue(undefined);
    const refreshSpy = vi.spyOn(window, 'dispatchEvent');

    renderWithToast(
      <AssistantCell
        content="Save this response"
        isLast
        canEdit
        projectId="proj-1"
        notebookId="nb-1"
        notebookTitle="Notes"
        assistantName="Helper"
        selectedAssistant="helper"
        onSaveToNotebook={onSaveToNotebook}
        conversationContext={{
          messageId: 'msg-1',
          conversationId: 'conv-1',
          totalMessages: 3,
        }}
      />,
    );

    await user.click(screen.getByLabelText('Save to notebook'));
    await user.click(screen.getByRole('button', { name: 'Confirm save to notebook' }));

    await waitFor(() => {
      expect(onSaveToNotebook).toHaveBeenCalled();
      const [files, folderPath, shouldIndex] = onSaveToNotebook.mock.calls[0];
      expect(files).toHaveLength(1);
      expect(files[0]).toBeInstanceOf(File);
      expect(folderPath).toBe('Output');
      expect(shouldIndex).toBe(false);
      expect(refreshSpy).toHaveBeenCalledWith(expect.objectContaining({ type: 'refresh-notebook-files' }));
    });

    refreshSpy.mockRestore();
  });

  it('dispatches select-notebook-file on mobile long press', async () => {
    vi.useFakeTimers();
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });

    const dispatchSpy = vi.spyOn(window, 'dispatchEvent');

    renderWithToast(
      <AssistantCell
        content="Files created"
        isLast={false}
        turnFilesCreated={['docs/report.md']}
        onTurnFileClick={vi.fn()}
      />,
    );

    const pill = screen.getByText('report.md');
    fireEvent.touchStart(pill, { touches: [{ clientX: 10, clientY: 10 }] });
    await vi.advanceTimersByTimeAsync(500);
    fireEvent.touchEnd(pill, { touches: [] });

    expect(dispatchSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'select-notebook-file',
        detail: { relativePath: 'docs/report.md' },
      }),
    );

    dispatchSpy.mockRestore();
    vi.useRealTimers();
  });

  it('updates mobile mode when window is resized', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1200 });
    const onTurnFileClick = vi.fn();

    renderWithToast(
      <AssistantCell
        content="Resize test"
        isLast={false}
        turnFilesModified={['notes/readme.md']}
        onTurnFileClick={onTurnFileClick}
      />,
    );

    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    fireEvent(window, new Event('resize'));

    fireEvent.click(screen.getByText('readme.md'));
    expect(onTurnFileClick).toHaveBeenCalledWith('notes/readme.md');
  });
});
