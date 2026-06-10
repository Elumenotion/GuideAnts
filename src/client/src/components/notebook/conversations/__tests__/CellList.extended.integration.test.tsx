import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { ToastProvider } from '../../../common/Toast';

vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'current-user-id', name: 'Current User', email: 'current@example.com' }),
    getUserById: vi.fn().mockImplementation((userId: string) => {
      if (userId === 'editor-user-id') {
        return Promise.resolve({ id: 'editor-user-id', name: 'Editor User', email: 'editor@example.com' });
      }
      return Promise.resolve({ id: userId, name: 'Cached User' });
    }),
    isCurrentUser: vi.fn().mockImplementation((userId: string) => Promise.resolve(userId === 'current-user-id')),
  },
}));

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: vi.fn(() => ({
    notebook: { id: 'notebook-1', projectId: 'project-1', title: 'Test Notebook' },
    projectId: 'project-1',
    notebookId: 'notebook-1',
    folderTree: null,
    uploadFiles: vi.fn().mockResolvedValue([]),
  })),
}));

vi.mock('../../../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({ folderTree: null }),
}));

const mockCurrentTurn = { assistantStepSection: null, toolCalls: [], toolResults: [] };
vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: vi.fn(() => ({
    currentTurn: mockCurrentTurn,
    currentAssistant: { name: 'GPT', avatarUrl: '' },
    userProfiles: {},
    pendingAttachments: [],
    addPendingAttachment: vi.fn(),
    removePendingAttachment: vi.fn(),
  })),
}));

const enableTurnBasedMode = vi.fn();
const groupMessagesByTurns = vi.fn();

vi.mock('../../../../store/conversationStore', () => ({
  useConversationStore: () => ({
    actions: {
      enableTurnBasedMode,
      groupMessagesByTurns,
    },
  }),
}));

if (!HTMLElement.prototype.scrollTo) {
  HTMLElement.prototype.scrollTo = function () {};
}

const BASE_PROPS = {
  isStreaming: false,
  streamingMode: 'at-rest' as const,
  draftUserContent: '',
  editingAssistantId: undefined as string | undefined,
  selectedAssistant: 'GPT',
  assistants: [{ name: 'GPT', avatarUrl: '' }],
  onDraftChange: vi.fn(),
  onSendMessage: vi.fn(),
  onEditAssistant: vi.fn(),
  onSaveAssistant: vi.fn().mockResolvedValue(undefined),
  onCancelEdit: vi.fn(),
  onUndo: vi.fn(),
  onAssistantSelect: vi.fn(),
  onPreviewFile: vi.fn(),
  conversationStarters: ['Hello!', 'Explain this code'],
  editError: undefined,
  isEditLoading: false,
  turnBasedMode: false,
  canEdit: true,
  canUndo: true,
  conversationId: 'convo-1',
};

const renderWithToast = (ui: React.ReactElement) =>
  render(<ToastProvider>{ui}</ToastProvider>);

describe('CellList extended integration', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    enableTurnBasedMode.mockClear();
    groupMessagesByTurns.mockClear();
  });

  it('calls onDraftChange when conversation starter clicked', () => {
    const onDraftChange = vi.fn();
    renderWithToast(
      <CellList {...BASE_PROPS} messages={[]} onDraftChange={onDraftChange} />
    );

    fireEvent.click(screen.getByText('Hello!'));
    expect(onDraftChange).toHaveBeenCalledWith('Hello!');
  });

  it('renders read-only conversation starters when canEdit is false', () => {
    renderWithToast(
      <CellList {...BASE_PROPS} messages={[]} canEdit={false} />
    );

    expect(screen.getByText('Hello!')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /hello/i })).not.toBeInTheDocument();
  });

  it('hides draft cell while editing assistant message', () => {
    renderWithToast(
      <CellList
        {...BASE_PROPS}
        messages={[]}
        editingAssistantId="a-editing"
      />
    );

    expect(screen.queryByRole('group', { name: 'Compose message' })).not.toBeInTheDocument();
  });

  it('renders workflow section for tool messages in a turn', async () => {
    const msgs: MessageDto[] = [
      {
        id: 'u1',
        role: 'user',
        content: 'Run tool',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'current-user-id',
      },
      {
        id: 't1',
        role: 'tool',
        content: 'tool output',
        created: new Date().toISOString(),
        isEdited: false,
      },
      {
        id: 'a1',
        role: 'assistant',
        content: 'Done',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'GPT',
      },
    ];

    renderWithToast(<CellList {...BASE_PROPS} messages={msgs} />);

    await waitFor(() => {
      expect(screen.getByText('Run tool')).toBeInTheDocument();
      expect(screen.getByText('Done')).toBeInTheDocument();
    });
  });

  it('shows streaming assistant cell when streaming', async () => {
    const msgs: MessageDto[] = [
      {
        id: 'u1',
        role: 'user',
        content: 'Question',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'current-user-id',
      },
      {
        id: 'streaming-1',
        role: 'assistant',
        content: 'Typing...',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'GPT',
      },
    ];

    renderWithToast(
      <CellList {...BASE_PROPS} messages={msgs} isStreaming streamingMode="observing" />
    );

    await waitFor(() => {
      expect(screen.getByText('Typing...')).toBeInTheDocument();
    });
  });

  it('does not send when canEdit is false', () => {
    const onSendMessage = vi.fn();
    renderWithToast(
      <CellList
        {...BASE_PROPS}
        messages={[{ id: 'u1', role: 'user', content: 'Hi', created: '', isEdited: false }]}
        canEdit={false}
        onSendMessage={onSendMessage}
      />
    );

    expect(screen.queryByRole('group', { name: 'Compose message' })).not.toBeInTheDocument();
    expect(onSendMessage).not.toHaveBeenCalled();
  });

  it('enables turn-based grouping when turnBasedMode is on', async () => {
    const msgs: MessageDto[] = [
      {
        id: 'u1',
        role: 'user',
        content: 'Hi',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'current-user-id',
      },
      {
        id: 'a1',
        role: 'assistant',
        content: 'Hello',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'GPT',
      },
    ];

    renderWithToast(
      <CellList {...BASE_PROPS} messages={msgs} turnBasedMode conversationId="convo-turn" />
    );

    await waitFor(() => {
      expect(enableTurnBasedMode).toHaveBeenCalledWith('convo-turn');
      expect(groupMessagesByTurns).toHaveBeenCalledWith('convo-turn');
    });
  });
});
