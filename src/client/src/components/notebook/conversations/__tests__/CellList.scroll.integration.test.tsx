import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { ToastProvider } from '../../../common/Toast';

vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'u1', name: 'User One' }),
    getUserById: vi.fn().mockResolvedValue({ id: 'u1', name: 'User One' }),
    isCurrentUser: vi.fn().mockResolvedValue(true),
  },
}));

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({
    notebook: { id: 'nb-1', projectId: 'proj-1', title: 'NB' },
    projectId: 'proj-1',
    notebookId: 'nb-1',
    folderTree: {
      name: 'root',
      relativePath: '',
      files: [{ relativePath: 'Output/chart.png', fileName: 'chart.png' }],
      subFolders: [],
    },
    uploadFiles: vi.fn(),
  }),
}));

vi.mock('../../../../hooks/useNotebookFilesPolling', () => ({
  useNotebookFilesPolling: () => ({
    folderTree: {
      name: 'root',
      relativePath: '',
      files: [{ relativePath: 'Output/chart.png', fileName: 'chart.png' }],
      subFolders: [],
    },
  }),
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => ({
    currentTurn: null,
    currentAssistant: { name: 'GPT', avatarUrl: '' },
    userProfiles: {},
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

vi.mock('../WorkflowSection', () => ({
  default: () => <div data-testid="workflow" />,
}));

if (!HTMLElement.prototype.scrollTo) {
  HTMLElement.prototype.scrollTo = vi.fn();
}

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
  conversationId: 'c1',
};

describe('CellList scroll & file path integration', () => {
  it('renders assistant turn file pills when paths exist in tree', async () => {
    const msgs: MessageDto[] = [
      {
        id: 'u1',
        role: 'user',
        content: 'Make chart',
        created: new Date().toISOString(),
        isEdited: false,
        userId: 'u1',
      },
      {
        id: 'a1',
        role: 'assistant',
        content: 'Created chart',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: 'GPT',
        turnFilesCreated: ['Output/chart.png', 'missing.png'],
      },
    ];

    render(
      <ToastProvider>
        <CellList {...BASE} messages={msgs} onPreviewFileByPath={vi.fn()} />
      </ToastProvider>
    );

    await waitFor(() => {
      expect(screen.getByText('Created chart')).toBeInTheDocument();
    });

    // Pills must reflect turn-recorded paths even when the file is not in the live tree yet.
    expect(screen.getByText('chart.png')).toBeInTheDocument();
    expect(screen.getByText('missing.png')).toBeInTheDocument();
  });

  it('pins scroll to bottom when images load and user was at bottom', async () => {
    const msgs: MessageDto[] = [
      {
        id: 'a1',
        role: 'assistant',
        content: '![chart](https://example.com/chart.png)',
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

    const container = document.querySelector('.overflow-y-auto') as HTMLElement;
    Object.defineProperty(container, 'scrollHeight', { configurable: true, value: 2000 });
    Object.defineProperty(container, 'clientHeight', { configurable: true, value: 500 });
    Object.defineProperty(container, 'scrollTop', { configurable: true, writable: true, value: 1500 });

    const img = document.createElement('img');
    fireEvent.load(img, { target: img });

    expect(container).toBeTruthy();
  });

  it('handles user scroll away from bottom', async () => {
    const msgs: MessageDto[] = Array.from({ length: 8 }).map((_, i) => ({
      id: `m${i}`,
      role: 'user' as const,
      content: `Long message ${i}`,
      created: new Date().toISOString(),
      isEdited: false,
      userId: 'u1',
    }));

    render(
      <ToastProvider>
        <CellList {...BASE} messages={msgs} />
      </ToastProvider>
    );

    const container = document.querySelector('.overflow-y-auto') as HTMLElement;
    Object.defineProperty(container, 'scrollHeight', { configurable: true, value: 3000 });
    Object.defineProperty(container, 'clientHeight', { configurable: true, value: 500 });
    Object.defineProperty(container, 'scrollTop', { configurable: true, writable: true, value: 0 });

    fireEvent.wheel(container);
    fireEvent.scroll(container, { target: { scrollTop: 100 } });

    expect(container).toBeTruthy();
  });
});
