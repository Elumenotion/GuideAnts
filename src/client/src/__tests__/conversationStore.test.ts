import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useConversationStore } from '../store/conversationStore';
import { MessageDto } from '../types/conversation';
import * as apiModule from '../services/api';

// Helper to reset store between tests
function resetStore() {
  const { setState } = useConversationStore as unknown as {
    setState: (state: any) => void;
  };
  setState({ entries: {} });
}

describe('conversationStore.fetch', () => {
  const projectId = 'proj-1';
  const notebookId = 'nb-1';
  const conversationId = 'convo-1';

  const sampleMessages: MessageDto[] = [
    {
      id: 'm1',
      role: 'user',
      content: 'Hello',
      created: new Date().toISOString(),
      isEdited: false,
    },
    {
      id: 'm2',
      role: 'assistant',
      content: 'Hi there!',
      created: new Date().toISOString(),
      isEdited: false,
    },
  ];

  beforeEach(() => {
    resetStore();
  });

  it('loads messages and sets status to idle', async () => {
    // Mock the API layer using ESM import
    const mockGet = vi
      .spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockResolvedValue({ messages: sampleMessages } as any);

    await useConversationStore.getState().actions.fetch(
      projectId,
      notebookId,
      conversationId,
    );

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry).toBeDefined();
    expect(entry.messages).toEqual(sampleMessages);
    expect(entry.status).toBe('idle');

    mockGet.mockRestore();
  });
}); 