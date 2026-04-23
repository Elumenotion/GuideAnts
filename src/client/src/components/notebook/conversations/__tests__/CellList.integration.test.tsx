import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { ToastProvider } from '../../../common/Toast';

// Mock userService so auto cell loads current user instantly
vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'current-user-id', name: 'Current User', email: 'current@example.com' }),
    getUserById: vi.fn().mockImplementation((userId: string) => {
      // Return different users based on ID
      if (userId === 'editor-user-id') {
        return Promise.resolve({ id: 'editor-user-id', name: 'Editor User', email: 'editor@example.com' });
      }
      if (userId === 'current-user-id') {
        return Promise.resolve({ id: 'current-user-id', name: 'Current User', email: 'current@example.com' });
      }
      return Promise.resolve(null);
    }),
    isCurrentUser: vi.fn().mockImplementation((userId: string) => {
      return Promise.resolve(userId === 'current-user-id');
    }),
  },
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

// Ensure scrollTo exists on HTMLElement for JSDOM
if (!HTMLElement.prototype.scrollTo) {
  // eslint-disable-next-line @typescript-eslint/no-empty-function
  HTMLElement.prototype.scrollTo = function () {};
}

const renderWithToast = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

const BASE_PROPS = {
  isStreaming: false,
  draftUserContent: '',
  editingAssistantId: undefined as string | undefined,
  selectedAssistant: undefined as string | undefined,
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
};

describe('CellList integration behaviour', () => {
  it('shows conversation starters when no messages', () => {
    renderWithToast(<CellList {...BASE_PROPS} messages={[]} />);

    expect(screen.getByText('Start a conversation')).toBeInTheDocument();
    // Ensure both starters rendered
    expect(screen.getByText('Hello!')).toBeInTheDocument();
    expect(screen.getByText('Explain this code')).toBeInTheDocument();
  });



  it('shows correct editor avatar for edited assistant messages', async () => {
    const msgs: MessageDto[] = [
      { 
        id: 'u1', 
        role: 'user', 
        content: 'Hi', 
        created: new Date().toISOString(), 
        isEdited: false,
        userId: 'current-user-id'
      },
      { 
        id: 'a1', 
        role: 'assistant', 
        content: 'Hello! This message was edited.', 
        created: new Date().toISOString(), 
        isEdited: true,
        userId: 'editor-user-id', // Different user than current user
        originalContent: 'Hello!',
        lastEditedAt: new Date().toISOString(),
        assistantName: 'GPT'
      },
    ];

    renderWithToast(<CellList {...BASE_PROPS} messages={msgs} />);

    // Wait for the async user info loading to complete
    await waitFor(() => {
      // Check that the editor avatar is displayed (UserAvatar with editor user info)
      // The UserAvatar should show "EU" (Editor User initials) not "CU" (Current User initials)
      const editorAvatar = screen.getByTitle('Editor User');
      expect(editorAvatar).toBeInTheDocument();
      expect(editorAvatar).toHaveTextContent('EU');
    });

    // Verify that the "Edited" badge is present
    expect(screen.getByText('Edited')).toBeInTheDocument();
  });

  it('shows no editor avatar for non-edited assistant messages', async () => {
    const msgs: MessageDto[] = [
      { 
        id: 'u1', 
        role: 'user', 
        content: 'Hi', 
        created: new Date().toISOString(), 
        isEdited: false,
        userId: 'current-user-id'
      },
      { 
        id: 'a1', 
        role: 'assistant', 
        content: 'Hello!', 
        created: new Date().toISOString(), 
        isEdited: false, // Not edited
        assistantName: 'GPT'
      },
    ];

    renderWithToast(<CellList {...BASE_PROPS} messages={msgs} />);

    // Wait for rendering to complete
    await waitFor(() => {
      // Should not show "Edited" badge for non-edited messages
      expect(screen.queryByText('Edited')).not.toBeInTheDocument();
    });

    // Should not show editor avatar (only single assistant avatar)
    expect(screen.queryByTitle('Editor User')).not.toBeInTheDocument();
  });
}); 