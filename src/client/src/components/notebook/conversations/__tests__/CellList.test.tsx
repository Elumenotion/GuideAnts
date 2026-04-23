import React from 'react';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { vi, describe, it, expect } from 'vitest';
import { ToastProvider } from '../../../common/Toast';

// Minimal mock for child cells to avoid deep rendering
vi.mock('../UserCell', () => ({
  default: ({ content }: { content: string }) => <div data-testid="user-cell">{content}</div>
}));
vi.mock('../AssistantCell', () => ({
  default: ({ content }: { content: string }) => <div data-testid="assistant-cell">{content}</div>
}));
vi.mock('../EditableAssistantCell', () => ({
  default: () => <div data-testid="editable-assistant-cell" />
}));
vi.mock('../DraftUserCell', () => ({
  default: () => <textarea data-testid="draft-input" />
}));

// Mock userService to avoid network calls
vi.mock('../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User' }),
    getUserById: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User' }),
    isCurrentUser: vi.fn().mockResolvedValue(true)
  }
}));

// Mock useNotebook hook
vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: vi.fn(() => ({
    notebook: {
      id: 'notebook-1',
      projectId: 'project-1',
      title: 'Test Notebook'
    },
    folderTree: null,
    uploadFiles: vi.fn().mockResolvedValue([])
  }))
}));

const baseProps = {
  isStreaming: false,
  draftUserContent: '',
  editingAssistantId: undefined,
  selectedAssistant: 'GPT',
  assistants: [{ name: 'GPT' }],
  conversationStarters: ['Hello!', 'How can I help?'],
  onDraftChange: vi.fn(),
  onSendMessage: vi.fn(),
  onEditAssistant: vi.fn(),
  onSaveAssistant: vi.fn(),
  onCancelEdit: vi.fn(),
  onUndo: vi.fn(),
  editError: undefined,
  isEditLoading: false
};

const renderWithToast = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

describe('CellList', () => {
  it('shows empty state and conversation starters when no messages', () => {
    renderWithToast(<CellList {...baseProps} messages={[]} />);
    expect(screen.getByText('Start a conversation')).toBeInTheDocument();
    // Should render starter buttons
    expect(screen.getByText('Hello!')).toBeInTheDocument();
  });

  it('renders user and assistant cells for messages', () => {
    const messages: MessageDto[] = [
      { id: '1', role: 'user', content: 'Hi', created: '', isEdited: false },
      { id: '2', role: 'assistant', content: 'Hello!', created: '', isEdited: false }
    ];

    renderWithToast(<CellList {...baseProps} messages={messages} />);

    expect(screen.getAllByTestId('user-cell')).toHaveLength(1);
    expect(screen.getAllByTestId('assistant-cell')).toHaveLength(1);
  });
}); 