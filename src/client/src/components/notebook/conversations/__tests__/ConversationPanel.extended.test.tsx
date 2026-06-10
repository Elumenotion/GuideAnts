import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ConversationPanel } from '../ConversationPanel';
import { createStubUseConversationValue } from '../../../../test/conversationContextStub';

const mockSendMessage = vi.fn();
const mockCellListProps = vi.fn();

vi.mock('../CellList', () => ({
  default: (props: Record<string, unknown>) => {
    mockCellListProps(props);
    return <div data-testid="cell-list" />;
  },
}));

vi.mock('../ConversationHeader', () => ({
  default: () => <div data-testid="header" />,
}));

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () =>
    createStubUseConversationValue({
      sendMessage: mockSendMessage,
      messages: [{ id: 'm1', role: 'user', content: 'hi', created: '', isEdited: false }],
      streamingError: 'Stream failed',
    }),
  ConversationProvider: ({ children }: { children: React.ReactNode }) => children,
}));

describe('ConversationPanel extended', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows missing chat model warning', () => {
    render(
      <ConversationPanel
        conversationId="c1"
        projectId="p1"
        notebookId="n1"
        isChatModelMissing
      />
    );
    expect(screen.getByText(/no chat model is configured/i)).toBeInTheDocument();
  });

  it('shows streaming error banner', () => {
    render(
      <ConversationPanel conversationId="c1" projectId="p1" notebookId="n1" canEdit />
    );
    expect(screen.getByText('Stream failed')).toBeInTheDocument();
  });

  it('blocks send when runtime is loading', () => {
    render(
      <ConversationPanel conversationId="c1" projectId="p1" notebookId="n1" canEdit isRuntimeLoading />
    );
    const props = mockCellListProps.mock.calls.at(-1)?.[0] as { canEdit: boolean };
    expect(props.canEdit).toBe(false);
  });
});
