import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import DraftUserCell from '../DraftUserCell';
import { PendingAttachment } from '../../../../types/conversation';

const mockAddPendingAttachment = vi.fn();
const mockRemovePendingAttachment = vi.fn();
let pendingAttachments: PendingAttachment[] = [];

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({ notebook: { id: 'nb-1', projectId: 'proj-1' } }),
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => ({
    pendingAttachments,
    addPendingAttachment: mockAddPendingAttachment,
    removePendingAttachment: mockRemovePendingAttachment,
  }),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    uploadFiles: vi.fn().mockResolvedValue([{ id: 'uploaded-1', fileName: 'pasted.png' }]),
  },
}));

vi.mock('../../../../hooks/useAudioRecorder', () => ({
  useAudioRecorder: () => ({
    isRecording: false,
    isProcessing: false,
    duration: 0,
    isSupported: false,
    startRecording: vi.fn(),
    stopRecording: vi.fn(),
  }),
}));

describe('DraftUserCell – attachments & mentions', () => {
  beforeEach(() => {
    pendingAttachments = [];
    mockAddPendingAttachment.mockClear();
    mockRemovePendingAttachment.mockClear();
  });

  it('shows pending attachments and removes on click', async () => {
    pendingAttachments = [
      { notebookFileId: 'f-1', fileName: 'diagram.png', uploadType: 'image' },
    ];

    const user = userEvent.setup();
    render(
      <DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />
    );

    expect(screen.getByText('diagram.png')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: '×' }));
    expect(mockRemovePendingAttachment).toHaveBeenCalledWith('f-1');
  });

  it('sends with attachments only when text is empty', async () => {
    pendingAttachments = [
      { notebookFileId: 'f-2', fileName: 'photo.jpg', uploadType: 'image' },
    ];
    const onSend = vi.fn();

    render(
      <DraftUserCell value="" onChange={vi.fn()} onSend={onSend} />
    );

    const sendButton = screen.getByRole('button', { name: 'Send' });
    expect(sendButton).not.toBeDisabled();
    fireEvent.click(sendButton);
    expect(onSend).toHaveBeenCalledWith('');
  });

  it('adds attachment on file drop', () => {
    render(
      <DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />
    );

    const composeGroup = screen.getByRole('group', { name: 'Compose message' });
    const dataTransfer = {
      getData: (type: string) => {
        if (type === 'application/x-notebook-file-id') return 'dropped-id';
        if (type === 'application/x-notebook-file-name') return 'notes.txt';
        return '';
      },
    };

    fireEvent.drop(composeGroup, { dataTransfer });
    expect(mockAddPendingAttachment).toHaveBeenCalledWith(
      expect.objectContaining({ notebookFileId: 'dropped-id', fileName: 'notes.txt' })
    );
  });

  it('opens assistant selector on @ key and selects guide', async () => {
    const onAssistantSelect = vi.fn();
    const user = userEvent.setup();

    render(
      <DraftUserCell
        value=""
        onChange={vi.fn()}
        onSend={vi.fn()}
        assistants={[
          { name: 'Guide Alpha', model: 'gpt-4' },
          { name: 'Guide Beta', model: 'gpt-4' },
        ]}
        onAssistantSelect={onAssistantSelect}
      />
    );

    const textbox = screen.getByRole('textbox');
    await user.click(textbox);
    await user.type(textbox, '@');

    await waitFor(() => {
      expect(screen.getByText(/Select Guide/)).toBeInTheDocument();
    });

    await user.click(screen.getByText('Guide Alpha'));
    expect(onAssistantSelect).toHaveBeenCalledWith('Guide Alpha');
  });

  it('filters assistants while selector is open', async () => {
    const user = userEvent.setup();

    render(
      <DraftUserCell
        value=""
        onChange={vi.fn()}
        onSend={vi.fn()}
        assistants={[
          { name: 'Alpha Guide', model: 'gpt-4' },
          { name: 'Beta Guide', model: 'claude' },
        ]}
      />
    );

    const editor = screen.getByRole('group', { name: 'Compose message' });
    const textbox = screen.getByRole('textbox');
    await user.click(textbox);
    await user.type(textbox, '@');

    await waitFor(() => expect(screen.getByText(/Select Guide/)).toBeInTheDocument());

    fireEvent.keyDown(editor.querySelector('.p-2') || editor, { key: 'b' });
    expect(screen.getByText('Beta Guide')).toBeInTheDocument();
    expect(screen.queryByText('Alpha Guide')).not.toBeInTheDocument();
  });

  it('closes assistant selector on Escape', async () => {
    const user = userEvent.setup();

    render(
      <DraftUserCell
        value=""
        onChange={vi.fn()}
        onSend={vi.fn()}
        assistants={[{ name: 'Guide', model: 'gpt-4' }]}
      />
    );

    await user.click(screen.getByRole('textbox'));
    await user.type(screen.getByRole('textbox'), '@');

    await waitFor(() => expect(screen.getByText(/Select Guide/)).toBeInTheDocument());

    const editor = screen.getByRole('group', { name: 'Compose message' });
    fireEvent.keyDown(editor.querySelector('.p-2') || editor, { key: 'Escape' });

    await waitFor(() => {
      expect(screen.queryByText(/Select Guide/)).not.toBeInTheDocument();
    });
  });

  it('closes assistant selector when clicking outside', async () => {
    const user = userEvent.setup();

    render(
      <DraftUserCell
        value=""
        onChange={vi.fn()}
        onSend={vi.fn()}
        assistants={[{ name: 'Guide', model: 'gpt-4' }]}
      />
    );

    await user.click(screen.getByRole('textbox'));
    await user.type(screen.getByRole('textbox'), '@');

    await waitFor(() => expect(screen.getByText(/Select Guide/)).toBeInTheDocument());

    fireEvent.mouseDown(document.body);
    await waitFor(() => {
      expect(screen.queryByText(/Select Guide/)).not.toBeInTheDocument();
    });
  });

  it('uploads pasted image and adds pending attachment', async () => {
    const { notebookFilesApi } = await import('../../../../services/notebookFiles');

    render(
      <DraftUserCell value="" onChange={vi.fn()} onSend={vi.fn()} />
    );

    const editorContainer = screen.getByRole('group', { name: 'Compose message' });
    const file = new File(['img'], 'paste.png', { type: 'image/png' });
    const clipboardData = {
      items: [{ kind: 'file', type: 'image/png', getAsFile: () => file }],
    };

    fireEvent.paste(editorContainer.querySelector('.p-2') || editorContainer, {
      clipboardData,
    });

    await waitFor(() => {
      expect(notebookFilesApi.uploadFiles).toHaveBeenCalled();
      expect(mockAddPendingAttachment).toHaveBeenCalledWith(
        expect.objectContaining({ notebookFileId: 'uploaded-1' })
      );
    });
  });
});
