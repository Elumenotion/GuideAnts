import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import path from 'path';

vi.mock('../AssistantSelector', () => ({
  __esModule: true,
  default: ({ selectedName, disabled }: { selectedName: string; disabled?: boolean }) => (
    <div data-testid="selector" data-disabled={disabled}>{selectedName}</div>
  ),
}));

const mockCreateConversation = vi.fn();
vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({
    createConversation: mockCreateConversation,
  }),
}));

const assistants = [
  { name: 'Alpha', model: '' },
  { name: 'Beta', model: '' },
];

beforeEach(() => {
  vi.resetModules();
  mockCreateConversation.mockReset();
});

async function renderHeader(
  conversationOverrides: Record<string, unknown>,
  props: Record<string, unknown> = {}
) {
  const ctxPath = path.resolve(__dirname, '../../../../contexts/ConversationContext.tsx');
  vi.doMock(ctxPath, () => ({
    useConversation: () => ({
      selectedAssistant: 'Beta',
      setSelectedAssistant: vi.fn(),
      assistants,
      isStreaming: false,
      isCancelling: false,
      cancelStream: vi.fn(),
      ...conversationOverrides,
    }),
  }));

  const { default: ConversationHeader } = await import('../ConversationHeader');
  return render(<ConversationHeader onUndo={() => {}} {...props} />);
}

describe('ConversationHeader – streaming & actions', () => {
  it('shows stop button while streaming and calls cancelStream', async () => {
    const cancelStream = vi.fn();
    await renderHeader({ isStreaming: true, cancelStream });

    const stopButton = screen.getByTitle('Stop generation');
    expect(stopButton).toBeInTheDocument();
    fireEvent.click(stopButton);
    expect(cancelStream).toHaveBeenCalled();
  });

  it('shows stopping state while cancelling', async () => {
    await renderHeader({ isStreaming: true, isCancelling: true });
    expect(screen.getByText('Stopping...')).toBeInTheDocument();
    expect(screen.getByTitle('Stopping...')).toBeDisabled();
  });

  it('creates a new conversation when mobile button clicked', async () => {
    mockCreateConversation.mockResolvedValue({ id: 'new-convo-1' });
    const onNewConversation = vi.fn();
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent');

    await renderHeader(
      { isStreaming: false, selectedAssistant: null },
      { canEdit: true, onNewConversation }
    );

    fireEvent.click(screen.getByLabelText('New conversation'));
    await vi.waitFor(() => expect(mockCreateConversation).toHaveBeenCalledWith('New Conversation'));
    expect(onNewConversation).toHaveBeenCalledWith('new-convo-1');
    expect(dispatchSpy).toHaveBeenCalled();
    dispatchSpy.mockRestore();
  });

  it('does not create conversation when canEdit is false', async () => {
    await renderHeader({ isStreaming: false }, { canEdit: false });
    expect(screen.queryByLabelText('New conversation')).not.toBeInTheDocument();
  });

  it('disables assistant selector when canEdit is false', async () => {
    await renderHeader({ selectedAssistant: null }, { canEdit: false });
    expect(screen.getByTestId('selector')).toHaveAttribute('data-disabled', 'true');
    expect(screen.getByTestId('selector')).toHaveTextContent('Alpha');
  });
});
