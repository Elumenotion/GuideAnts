import React from 'react';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { vi, describe, it, expect } from 'vitest';
import { ToastProvider } from '../../../common/Toast';

vi.mock('../UserCell', () => ({ default: ({ content }: { content: string }) => <div data-testid="user-cell">{content}</div> }));
vi.mock('../AssistantCell', () => ({ default: ({ content }: { content: string }) => <div data-testid="assistant-cell">{content}</div> }));
vi.mock('../EditableAssistantCell', () => ({ default: () => <div data-testid="editable-assistant-cell" /> }));
vi.mock('../DraftUserCell', () => ({ default: () => <textarea data-testid="draft-input" /> }));
vi.mock('../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User' }),
    getUserById: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User' }),
    isCurrentUser: vi.fn().mockResolvedValue(true),
  },
}));
vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: vi.fn(() => ({ notebook: { id: 'notebook-1', projectId: 'project-1', title: 'Test Notebook' }, folderTree: null, uploadFiles: vi.fn().mockResolvedValue([]) })),
}));

const baseProps = {
  isStreaming: false,
  draftUserContent: '',
  editingAssistantId: undefined,
  selectedAssistant: 'GPT',
  assistants: [{ name: 'GPT' }],
  conversationStarters: [],
  onDraftChange: vi.fn(),
  onSendMessage: vi.fn(),
  onEditAssistant: vi.fn(),
  onSaveAssistant: vi.fn(),
  onAssistantSelect: vi.fn(),
  onUndo: vi.fn(),
  editError: undefined,
  isEditLoading: false,
};

function messagesForTurns(turnIndexes: number[]): MessageDto[] {
  return turnIndexes.flatMap((turnIndex, i) => [
    { id: `u${i}`, role: 'user' as const, content: `user ${turnIndex}`, created: '2026-01-01T00:00:00Z', isEdited: false, turnIndex },
    { id: `a${i}`, role: 'assistant' as const, content: `assistant ${turnIndex}`, created: '2026-01-01T00:00:01Z', isEdited: false, turnIndex },
  ]);
}

describe('CellList compaction boundary marker', () => {
  it('renders the marker immediately before the first turn after the boundary', () => {
    render(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3])} compactionBoundaryTurnIndex={2} />,
      { wrapper: ToastProvider },
    );

    expect(screen.getByTestId('compaction-boundary-marker')).toBeInTheDocument();
  });

  it('renders no marker when the conversation has never been compacted', () => {
    render(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3])} compactionBoundaryTurnIndex={null} />,
      { wrapper: ToastProvider },
    );

    expect(screen.queryByTestId('compaction-boundary-marker')).not.toBeInTheDocument();
  });

  it('moves to the new boundary after a second compaction', () => {
    const { rerender } = render(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3, 4, 5])} compactionBoundaryTurnIndex={2} />,
      { wrapper: ToastProvider },
    );
    expect(screen.getAllByTestId('compaction-boundary-marker')).toHaveLength(1);

    rerender(
      <CellList {...baseProps} messages={messagesForTurns([1, 2, 3, 4, 5])} compactionBoundaryTurnIndex={4} />,
    );

    const markers = screen.getAllByTestId('compaction-boundary-marker');
    expect(markers).toHaveLength(1);
  });
});
