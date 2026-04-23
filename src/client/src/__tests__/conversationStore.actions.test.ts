import { describe, it, expect, vi, beforeEach } from 'vitest';
import { useConversationStore } from '../store/conversationStore';
import * as apiModule from '../services/api';

// Utility to reset Zustand store state between tests to avoid cross-test bleed-through
function resetStore() {
  const { setState } = useConversationStore as unknown as {
    setState: (state: any) => void;
  };
  setState({ entries: {} });
}

describe('conversationStore additional actions', () => {
  const projectId = 'proj-1';
  const notebookId = 'nb-1';
  const conversationId = 'convo-1';

  beforeEach(() => {
    resetStore();
    vi.restoreAllMocks();
  });

  // ---------------------------------------------------------------------------
  it('sets status to error when fetch throws', async () => {
    const errorMessage = 'Network down';
    vi
      .spyOn(apiModule.api.projects.notebooks.conversations, 'get')
      .mockRejectedValueOnce(new Error(errorMessage));

    await useConversationStore.getState().actions.fetch(
      projectId,
      notebookId,
      conversationId,
    );

    const entry = useConversationStore.getState().entries[conversationId];
    expect(entry.status).toBe('error');
    expect(entry.error).toBe(errorMessage);
  });

  // ---------------------------------------------------------------------------
  it('invalidate removes an entry from the cache', async () => {
    // prime the cache
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          status: 'idle',
        },
      },
    });

    expect(useConversationStore.getState().entries).toHaveProperty(conversationId);

    // act
    useConversationStore.getState().actions.invalidate(conversationId);

    expect(useConversationStore.getState().entries).not.toHaveProperty(conversationId);
  });

  // ---------------------------------------------------------------------------
  it('cancelStream aborts the controller and resets status', () => {
    const abortController = new AbortController();
    // create a "streaming" entry to cancel
    useConversationStore.setState({
      entries: {
        [conversationId]: {
          messages: [],
          status: 'streaming',
          abortController,
        },
      },
    });

    const abortSpy = vi.spyOn(abortController, 'abort');

    useConversationStore.getState().actions.cancelStream(conversationId);

    // abort() should have been invoked exactly once
    expect(abortSpy).toHaveBeenCalledTimes(1);

    const updated = useConversationStore.getState().entries[conversationId];
    expect(updated.abortController).toBeUndefined();
    expect(updated.status).toBe('idle');
  });
}); 