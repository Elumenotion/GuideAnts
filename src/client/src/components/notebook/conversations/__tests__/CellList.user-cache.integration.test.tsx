import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { ToastProvider } from '../../../common/Toast';

const { getUserById, isCurrentUser, getCurrentUser } = vi.hoisted(() => ({
  getUserById: vi.fn(),
  isCurrentUser: vi.fn(),
  getCurrentUser: vi.fn(),
}));

vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser,
    getUserById,
    isCurrentUser,
  },
}));

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({
    notebook: { id: 'nb-1', projectId: 'proj-1', title: 'NB' },
    projectId: 'proj-1',
    notebookId: 'nb-1',
    uploadFiles: vi.fn(),
  }),
}));

vi.mock('../../../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({ folderTree: null }),
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => ({
    currentTurn: null,
    currentAssistant: { name: 'GPT' },
    userProfiles: { 'cached-id': { id: 'cached-id', name: 'Cached', email: 'c@x.com' } },
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

const BASE = {
  isStreaming: false,
  streamingMode: 'at-rest' as const,
  draftUserContent: '',
  assistants: [{ name: 'GPT' }],
  conversationStarters: [],
  onDraftChange: vi.fn(),
  onSendMessage: vi.fn(),
  onEditAssistant: vi.fn(),
  onSaveAssistant: vi.fn(),
  onCancelEdit: vi.fn(),
  onUndo: vi.fn(),
  onAssistantSelect: vi.fn(),
  canEdit: true,
};

describe('CellList user cache integration', () => {
  it('preloads user info for message authors', async () => {
    getCurrentUser.mockResolvedValue({ id: 'current', name: 'Current' });
    isCurrentUser.mockResolvedValue(false);
    getUserById.mockResolvedValue({ id: 'other-user', name: 'Other User', email: 'o@x.com' });

    const msgs: MessageDto[] = [
      {
        id: 'u1',
        role: 'user',
        content: 'Hello',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'other-user',
      },
      {
        id: 'a1',
        role: 'assistant',
        content: 'Hi',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'GPT',
      },
    ];

    render(
      <ToastProvider>
        <CellList {...BASE} messages={msgs} />
      </ToastProvider>
    );

    await waitFor(() => expect(getUserById).toHaveBeenCalled());
  });

  it('renders assistant-only synthetic turn', async () => {
    getCurrentUser.mockResolvedValue({ id: 'current', name: 'Current' });
    isCurrentUser.mockResolvedValue(true);
    getUserById.mockResolvedValue(null);

    const msgs: MessageDto[] = [
      {
        id: 'a-only',
        role: 'assistant',
        content: 'Orphan assistant message',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'GPT',
      },
    ];

    render(
      <ToastProvider>
        <CellList {...BASE} messages={msgs} selectedAssistant="GPT" />
      </ToastProvider>
    );

    await waitFor(() => {
      expect(screen.getByText('Orphan assistant message')).toBeInTheDocument();
    });
  });
});
