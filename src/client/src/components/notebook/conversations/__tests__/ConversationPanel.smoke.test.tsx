import React from 'react';
import { render, waitFor } from '../../../../test/test-utils';
import { ConversationProvider } from '../../../../contexts/ConversationContext';
import { ConversationPanel } from '../ConversationPanel';
import { vi, describe, it, expect } from 'vitest';

// Mock the API layer to avoid real network calls
vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebookTemplates: {
        getAll: vi.fn().mockResolvedValue([]),
        getAssistants: vi.fn().mockResolvedValue([])
      },
      notebooks: {
        conversations: {
          get: vi.fn().mockResolvedValue({ messages: [] }),
          sendMessageStream: vi.fn()
        }
      }
    }
  }
}));

// Mock user service to bypass auth dependency
vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({ id: 'u1', name: 'Test User', email: 'test@example.com' }),
    isCurrentUser: vi.fn().mockResolvedValue(true),
    getUserById: vi.fn().mockResolvedValue(null)
  }
}));

/**
 * Basic smoke test – renders ConversationPanel with no messages and
 * asserts that the empty-state starter UI is shown.
 */

describe('ConversationPanel – smoke', () => {
  it('renders empty state when there are no messages', async () => {
    const { getByText } = render(
      <ConversationProvider
        projectId="p1"
        notebookId="nb1"
        conversationId="c1"
        notebookTemplateId="tpl1"
      >
        {/* @ts-ignore – prop not used internally but satisfies signature */}
        <ConversationPanel conversationId="c1" />
      </ConversationProvider>
    );

    // Wait for context initialisation
    await waitFor(() => {
      expect(getByText(/start a conversation/i)).toBeInTheDocument();
    });
  });
}); 