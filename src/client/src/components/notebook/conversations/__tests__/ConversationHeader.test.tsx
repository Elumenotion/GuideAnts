import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import path from 'path';

// Stub AssistantSelector (hoisted ok)
vi.mock('../AssistantSelector', () => ({
  __esModule: true,
  default: ({ selectedName }: { selectedName: string }) => (
    <div data-testid="selector">{selectedName}</div>
  ),
}));

vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({
    createConversation: vi.fn(),
  }),
}));

const assistants = [
  { name: 'Alpha', model: '' },
  { name: 'Beta', model: '' },
];

beforeEach(() => {
  vi.resetModules();
});

describe('ConversationHeader', () => {
  it('shows current assistant when selected', async () => {
    const ctxPath = path.resolve(__dirname, '../../../../contexts/ConversationContext.tsx');
    vi.doMock(ctxPath, () => ({
      useConversation: () => ({
        selectedAssistant: 'Beta',
        setSelectedAssistant: vi.fn(),
        assistants,
        isStreaming: false,
        isCancelling: false,
        cancelStream: vi.fn(),
      }),
    }));

    const { default: ConversationHeader } = await import('../ConversationHeader');
    const { getByTestId } = render(<ConversationHeader onUndo={() => {}} />);
    expect(getByTestId('selector')).toHaveTextContent('Beta');
  });

  it('falls back to first assistant when none selected', async () => {
    const ctxPath = path.resolve(__dirname, '../../../../contexts/ConversationContext.tsx');
    vi.doMock(ctxPath, () => ({
      useConversation: () => ({
        selectedAssistant: null,
        setSelectedAssistant: vi.fn(),
        assistants,
        isStreaming: false,
        isCancelling: false,
        cancelStream: vi.fn(),
      }),
    }));

    const { default: ConversationHeader } = await import('../ConversationHeader');
    const { getByTestId } = render(<ConversationHeader onUndo={() => {}} />);
    expect(getByTestId('selector')).toHaveTextContent('Alpha');
  });
}); 