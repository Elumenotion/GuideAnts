import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import AssistantCell from '../AssistantCell';
import { ToastProvider } from '../../../common/Toast';

vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>,
}));

vi.mock('../../dialogs/SaveAssistantContentDialog', () => ({
  SaveAssistantContentDialog: ({ isOpen }: { isOpen: boolean }) =>
    isOpen ? <div data-testid="save-assistant-dialog">Save dialog open</div> : null,
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

describe('AssistantCell – interactions', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1024 });
  });

  it('shows edit button when last message and canEdit', () => {
    const onEditClick = vi.fn();
    renderWithToast(
      <AssistantCell
        content="Editable reply"
        isLast
        canEdit
        onEditClick={onEditClick}
      />
    );

    expect(screen.getByLabelText('Edit message')).toBeInTheDocument();
  });

  it('opens fullscreen editor from edit button', async () => {
    const user = userEvent.setup();
    renderWithToast(
      <AssistantCell
        content="Editable reply"
        isLast
        canEdit
        onEditClick={vi.fn()}
        onSave={vi.fn()}
      />
    );

    await user.click(screen.getByLabelText('Edit message'));

    await waitFor(() => {
      expect(screen.getByText('Edit Message')).toBeInTheDocument();
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });
  });

  it('renders turn file pills for created and modified files', async () => {
    const user = userEvent.setup();
    const onTurnFileClick = vi.fn();
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent');

    renderWithToast(
      <AssistantCell
        content="Created a file"
        isLast={false}
        turnFilesCreated={['Output/report.md']}
        turnFilesModified={['notes/readme.md', 'Output/report.md']}
        onTurnFileClick={onTurnFileClick}
      />
    );

    expect(screen.getByText('report.md')).toBeInTheDocument();
    expect(screen.getByText('readme.md')).toBeInTheDocument();

    await user.click(screen.getByText('report.md'));
    expect(dispatchSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'select-notebook-file',
        detail: { relativePath: 'Output/report.md' },
      })
    );

    dispatchSpy.mockRestore();
  });

  it('opens fullscreen viewer with markdown content', async () => {
    const user = userEvent.setup();
    renderWithToast(
      <AssistantCell content="Long assistant response" isLast />
    );

    await user.click(screen.getByLabelText('Full screen'));

    await waitFor(() => {
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
      expect(screen.getAllByTestId('markdown-viewer')[0]).toHaveTextContent('Long assistant response');
    });
  });

  it('shows save button when save props are provided', () => {
    renderWithToast(
      <AssistantCell
        content="Save me"
        isLast
        canEdit
        projectId="p1"
        notebookId="nb1"
        onSaveToNotebook={vi.fn()}
        conversationContext={{
          messageId: 'm1',
          conversationId: 'c1',
          totalMessages: 2,
        }}
      />
    );

    expect(screen.getByLabelText('Save to notebook')).toBeInTheDocument();
  });

  it('hides edit button when canEdit is false', () => {
    renderWithToast(
      <AssistantCell
        content="Locked reply"
        isLast
        canEdit={false}
        onEditClick={vi.fn()}
      />
    );

    expect(screen.queryByLabelText('Edit message')).not.toBeInTheDocument();
  });

  it('opens save dialog when save button is clicked', async () => {
    const user = userEvent.setup();
    renderWithToast(
      <AssistantCell
        content="Save me"
        isLast
        canEdit
        onSaveToNotebook={vi.fn()}
        conversationContext={{ messageId: 'm1', conversationId: 'c1', totalMessages: 2 }}
      />
    );

    await user.click(screen.getByLabelText('Save to notebook'));
    expect(screen.getByTestId('save-assistant-dialog')).toBeInTheDocument();
  });

  it('uses mobile tap to preview turn files', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 500 });
    const user = userEvent.setup();
    const onTurnFileClick = vi.fn();

    renderWithToast(
      <AssistantCell
        content="Created files"
        isLast={false}
        turnFilesCreated={['docs/report.md']}
        onTurnFileClick={onTurnFileClick}
      />
    );

    await user.click(screen.getByText('report.md'));
    expect(onTurnFileClick).toHaveBeenCalledWith('docs/report.md');
  });

  it('uses desktop double-click to preview turn files', async () => {
    Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: 1200 });
    const onTurnFileClick = vi.fn();

    renderWithToast(
      <AssistantCell
        content="Modified files"
        isLast={false}
        turnFilesModified={['notes/readme.md']}
        onTurnFileClick={onTurnFileClick}
      />
    );

    fireEvent.doubleClick(screen.getByText('readme.md'));
    expect(onTurnFileClick).toHaveBeenCalledWith('notes/readme.md');
  });

  it('renders assistant avatar image when avatarUrl is provided', () => {
    renderWithToast(
      <AssistantCell
        content="With avatar"
        isLast={false}
        avatarUrl="https://example.com/avatar.png"
        assistantName="Helper"
      />
    );

    expect(screen.getByAltText('Helper')).toHaveAttribute('src', 'https://example.com/avatar.png');
  });

  it('appends projectId to relative api avatar urls', () => {
    renderWithToast(
      <AssistantCell
        content="Relative avatar"
        isLast={false}
        avatarUrl="/api/assistants/avatar/helper"
        projectId="proj-99"
        assistantName="Helper"
      />
    );

    const img = screen.getByAltText('Helper');
    expect(img.getAttribute('src')).toContain('projectId=proj-99');
  });

  it('shows thinking indicator while streaming with empty content', () => {
    renderWithToast(
      <AssistantCell content="" isLast isStreaming assistantName="Claude" />,
    );

    expect(screen.getByText('Thinking...')).toBeInTheDocument();
  });

  it('renders edited avatars and opens original overlay from assistant avatar', async () => {
    const user = userEvent.setup();
    renderWithToast(
      <AssistantCell
        content="Edited answer"
        isLast={false}
        isEdited
        originalContent="Original answer"
        editorUserName="Doug"
        editorUserEmail="doug@example.com"
        lastEditedAt="2026-06-09T12:00:00.000Z"
        assistantName="Helper"
      />,
    );

    expect(screen.getAllByLabelText('View original assistant response').length).toBeGreaterThan(0);
    await user.click(screen.getAllByLabelText('View original assistant response')[0]);
    expect(screen.getByText('Original Assistant Message')).toBeInTheDocument();
    expect(screen.getByText(/Edited by Doug on/i)).toBeInTheDocument();
  });

  it('shows edit control in fullscreen viewer for last editable message', async () => {
    const user = userEvent.setup();
    renderWithToast(
      <AssistantCell
        content="Fullscreen editable"
        isLast
        canEdit
        onEditClick={vi.fn()}
        onSave={vi.fn()}
      />,
    );

    await user.click(screen.getByLabelText('Full screen'));
    await waitFor(() => {
      expect(screen.getByLabelText('Exit full screen')).toBeInTheDocument();
    });

    await user.click(screen.getByLabelText('Edit message'));
    await waitFor(() => {
      expect(screen.getByText('Edit Message')).toBeInTheDocument();
    });
  });
});
