import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import '@testing-library/jest-dom';
import { ConversationPanel } from '../ConversationPanel';
import { createStubUseConversationValue } from '../../../../test/conversationContextStub';
import { MessageDto } from '../../../../types/conversation';

const mockUseConversation = vi.fn();

vi.mock('../../../../contexts/ConversationContext', () => ({
  ConversationProvider: ({ children }: { children: React.ReactNode }) => children,
  useConversation: () => mockUseConversation(),
}));

vi.mock('../ConversationHeader', () => ({
  default: ({ onUndo, canEdit }: { onUndo: () => void; canEdit: boolean }) => (
    <div data-testid="conversation-header">
      <button type="button" onClick={onUndo} disabled={!canEdit}>
        Undo
      </button>
    </div>
  ),
}));

vi.mock('../CellList', () => ({
  default: ({
    messages,
    canEdit,
    onSendMessage,
  }: {
    messages: MessageDto[];
    canEdit: boolean;
    onSendMessage: (content: string) => void;
  }) => (
    <div data-testid="cell-list">
      <span data-testid="message-count">{messages.length}</span>
      <span data-testid="can-edit">{String(canEdit)}</span>
      <button type="button" onClick={() => onSendMessage('hello')} disabled={!canEdit}>
        Send
      </button>
    </div>
  ),
}));

const sampleMessages: MessageDto[] = [
  { id: '1', role: 'user', content: 'Hi', created: '', isEdited: false },
  { id: '2', role: 'assistant', content: 'Hello', created: '', isEdited: false },
];

describe('ConversationPanel', () => {
  beforeEach(() => {
    mockUseConversation.mockReturnValue(createStubUseConversationValue());
  });

  it('renders cell list with messages from context', () => {
    mockUseConversation.mockReturnValue(
      createStubUseConversationValue({ messages: sampleMessages })
    );

    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="nb1"
        canEdit
      />
    );

    expect(screen.getByTestId('message-count')).toHaveTextContent('2');
    expect(screen.getByTestId('can-edit')).toHaveTextContent('true');
  });

  it('shows no chat model warning banner', () => {
    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="nb1"
        canEdit
        isChatModelMissing
      />
    );

    expect(screen.getByText(/No chat model is configured/i)).toBeInTheDocument();
    expect(screen.getByTestId('can-edit')).toHaveTextContent('false');
  });

  it('shows streaming error banner from context', () => {
    mockUseConversation.mockReturnValue(
      createStubUseConversationValue({ streamingError: 'Model request failed' })
    );

    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="nb1"
        canEdit
      />
    );

    expect(screen.getByText('Model request failed')).toBeInTheDocument();
  });

  it('blocks send when runtime is loading', () => {
    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="nb1"
        canEdit
        isRuntimeLoading
      />
    );

    expect(screen.getByTestId('can-edit')).toHaveTextContent('false');
  });

  it('blocks send when undo is in flight but keeps undo enabled when only undoing', () => {
    mockUseConversation.mockReturnValue(
      createStubUseConversationValue({ isUndoing: true, messages: sampleMessages })
    );

    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="nb1"
        canEdit
      />
    );

    expect(screen.getByTestId('can-edit')).toHaveTextContent('false');
    expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled();
  });

  it('allows undo when edit is permitted and not undoing', () => {
    mockUseConversation.mockReturnValue(
      createStubUseConversationValue({ messages: sampleMessages, isUndoing: false })
    );

    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="nb1"
        canEdit
        isChatModelMissing
      />
    );

    expect(screen.getByRole('button', { name: 'Undo' })).not.toBeDisabled();
  });
});
