import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { ToastProvider } from '../../../common/Toast';

const mockGetUserById = vi.fn();
const mockGetCurrentUser = vi.fn();

vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: (...args: unknown[]) => mockGetCurrentUser(...args),
    getUserById: (...args: unknown[]) => mockGetUserById(...args),
    isCurrentUser: vi.fn().mockResolvedValue(false),
  },
}));

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({
    notebook: { id: 'nb-1', projectId: 'proj-1', title: 'Notebook' },
    projectId: 'proj-1',
    notebookId: 'nb-1',
    folderTree: null,
    uploadFiles: vi.fn(),
  }),
}));

vi.mock('../../../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({ folderTree: null }),
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => ({
    currentTurn: null,
    currentAssistant: { name: 'GPT', avatarUrl: '' },
    userProfiles: {
      'cached-user': { name: 'Cached Name', email: 'cached@example.com' },
    },
    pendingAttachments: [],
    addPendingAttachment: vi.fn(),
    removePendingAttachment: vi.fn(),
  }),
}));

vi.mock('../../../../store/conversationStore', () => ({
  useConversationStore: () => ({
    actions: { enableTurnBasedMode: vi.fn(), groupMessagesByTurns: vi.fn() },
  }),
}));

if (!HTMLElement.prototype.scrollTo) {
  HTMLElement.prototype.scrollTo = vi.fn();
}

const BASE = {
  isStreaming: false,
  streamingMode: 'at-rest' as const,
  draftUserContent: '',
  assistants: [{ name: 'GPT', avatarUrl: '' }],
  conversationStarters: [],
  onDraftChange: vi.fn(),
  onSendMessage: vi.fn(),
  onEditAssistant: vi.fn(),
  onSaveAssistant: vi.fn().mockResolvedValue(undefined),
  onCancelEdit: vi.fn(),
  onUndo: vi.fn(),
  onAssistantSelect: vi.fn(),
  canEdit: true,
  conversationId: 'convo-1',
};

const renderList = (ui: React.ReactElement) =>
  render(<ToastProvider>{ui}</ToastProvider>);

describe('CellList – messages & user cache', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetCurrentUser.mockResolvedValue({ id: 'current-user', name: 'Current User' });
    mockGetUserById.mockImplementation((userId: string) =>
      Promise.resolve({ id: userId, name: `User ${userId}` })
    );
  });

  it('renders user message with attachments and calls onPreviewFile', async () => {
    const onPreviewFile = vi.fn();
    const msgs: MessageDto[] = [
      {
        id: 'u1',
        role: 'user',
        content: 'See attachment',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'user-1',
        attachments: [{ notebookFileId: 'file-1', fileName: 'notes.txt' }],
      },
    ];

    renderList(
      <CellList {...BASE} messages={msgs} onPreviewFile={onPreviewFile} />
    );

    await waitFor(() => {
      expect(screen.getByText('See attachment')).toBeInTheDocument();
      expect(screen.getByText('notes.txt')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('notes.txt'));
    expect(onPreviewFile).toHaveBeenCalledWith('file-1');
  });

  it('keeps draft cell visible while streaming in at-rest mode', () => {
    renderList(
      <CellList
        {...BASE}
        messages={[]}
        isStreaming
        streamingMode="at-rest"
      />
    );

    expect(screen.getByRole('group', { name: 'Compose message' })).toBeInTheDocument();
  });

  it('renders assistant message even when assistant list is still loading', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    const msgs: MessageDto[] = [
      {
        id: 'a-missing',
        role: 'assistant',
        content: 'From unknown assistant',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'MissingGuide',
      },
    ];

    renderList(
      <CellList {...BASE} messages={msgs} assistants={[]} />
    );

    await waitFor(() => {
      expect(screen.getByText('From unknown assistant')).toBeInTheDocument();
    });

    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });

  it('uses cached user profile from conversation context', async () => {
    const msgs: MessageDto[] = [
      {
        id: 'u-cache',
        role: 'user',
        content: 'Cached profile message',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'cached-user',
      },
    ];

    renderList(<CellList {...BASE} messages={msgs} />);

    await waitFor(() => {
      expect(screen.getByText('Cached profile message')).toBeInTheDocument();
    });
  });
});
