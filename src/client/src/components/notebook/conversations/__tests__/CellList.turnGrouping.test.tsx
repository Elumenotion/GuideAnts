import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import React from 'react';
import { ToastProvider } from '../../../common/Toast';
import { BrowserRouter } from 'react-router-dom';

// --- mocks ------------------------------------------------
vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'u-current' }),
    getUserById: vi.fn().mockResolvedValue(null),
    isCurrentUser: vi.fn().mockResolvedValue(false),
  },
}));

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({ notebook: null, folderTree: null, uploadFiles: vi.fn() }),
}));

vi.mock('../../../../components/common/ConfirmationDialog', () => ({
  ConfirmationDialog: ({ isOpen, onConfirm }: { isOpen: boolean; onConfirm: () => void }) =>
    isOpen ? <button data-testid="confirm" onClick={onConfirm}>confirm</button> : null,
}));

vi.mock('../../../../components/common/Toast', () => ({
  useToast: () => ({ showToast: () => {} }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

// ---------------------------------------------------------
function createMsg(id: string, role: 'user' | 'assistant', content: string): MessageDto {
  return {
    id,
    role,
    content,
    created: new Date().toISOString(),
    isEdited: false,
  } as MessageDto;
}

describe('CellList turn grouping', () => {
  it('groups assistant messages into thinking + final response and shows undo', async () => {
    const messages: MessageDto[] = [
      createMsg('u1', 'user', 'If I have 4 stone weight of peaches, how many grams is that?'),
      createMsg('a1', 'assistant', 'I called the tool named runPython ...'), // thinking 1
      createMsg('a2', 'assistant', 'tool output ...'), // thinking 2
      createMsg('a3', 'assistant', '4 stone = 25 401.16 grams'), // final
    ];

    const onUndo = vi.fn();

    render(
      <ToastProvider>
        <BrowserRouter>
          <CellList
            messages={messages}
            isStreaming={false}
            draftUserContent=""
            editingAssistantId={undefined}
            selectedAssistant="Python Ants"
            assistants={[{ name: 'Python Ants' }]}
            conversationStarters={[]}
            onDraftChange={vi.fn()}
            onSendMessage={vi.fn()}
            onEditAssistant={vi.fn()}
            onSaveAssistant={vi.fn()}
            onUndo={onUndo}
            onEditUserMessage={vi.fn()}
            editError={undefined}
            isEditLoading={false}
            conversationId="conv1"
          />
        </BrowserRouter>
      </ToastProvider>,
    );

    // User message rendered
    expect(screen.getByText(/peaches, how many grams/i)).toBeInTheDocument();

    // Workflow section should be present (combines thinking messages)
    expect(screen.getByTestId('workflow-section')).toBeInTheDocument();

    // Final assistant response visible
    expect(screen.getByText(/25 401\.16 grams/i)).toBeInTheDocument();

    // Undo button is present
    const undoBtn = screen.getByLabelText(/undo last turn/i);
    fireEvent.click(undoBtn);

    // click confirm in mocked dialog
    fireEvent.click(await screen.findByTestId('confirm'));

    expect(onUndo).toHaveBeenCalled();
  });
}); 