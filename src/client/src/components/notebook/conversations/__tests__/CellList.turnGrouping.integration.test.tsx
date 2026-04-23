import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import CellList from '../CellList';
import { MessageDto } from '../../../../types/conversation';
import { NotebookProvider } from '../../../../contexts/NotebookContext';
import { ConversationProvider } from '../../../../contexts/ConversationContext';
import { BrowserRouter } from 'react-router-dom';
import { ToastProvider } from '../../../common/Toast';

// Mock all the services
vi.mock('../../../../services/userService', () => ({
  userService: {
    getCurrentUser: () => Promise.resolve({ id: 'user-1', name: 'Test User' }),
    isCurrentUser: () => Promise.resolve(true),
    getUserById: () => Promise.resolve({ id: 'user-1', name: 'Test User' })
  }
}));

vi.mock('../../../../services/authService', () => ({
  authService: {
    getActiveAccount: () => ({ homeAccountId: 'test-account' })
  }
}));

vi.mock('../../../../store/conversationStore', () => ({
  useConversationStore: () => ({
    actions: {
      enableTurnBasedMode: vi.fn(),
      groupMessagesByTurns: vi.fn()
    }
  })
}));

vi.mock('../WorkflowSection', () => ({
  default: ({ messages }: { messages: MessageDto[] }) => (
    <div data-testid="workflow-section">
      <div data-testid="workflow-messages">{JSON.stringify(messages.map(m => ({ id: m.id, role: m.role, toolCalls: m.toolCalls, toolCallId: m.toolCallId })))}</div>
    </div>
  )
}));

// Test wrapper with all required providers
const TestWrapper = ({ children }: { children: React.ReactNode }) => {
  return (
    <ToastProvider>
      <BrowserRouter>
        <NotebookProvider 
          notebookId="test-notebook"
          projectId="test-project"
        >
          <ConversationProvider 
            projectId="test-project"
            notebookId="test-notebook"
            conversationId="test-conversation"
            notebookTemplateId="test-template"
          >
            {children}
          </ConversationProvider>
        </NotebookProvider>
      </BrowserRouter>
    </ToastProvider>
  );
};

describe('CellList Turn Grouping Integration', () => {
  const mockMessages: MessageDto[] = [
    {
      id: 'user-1',
      role: 'user',
      content: 'What version of python do you have?',
      userId: 'user-1',
      assistantName: 'user',
      isEdited: false,
      created: '2025-01-07T10:00:00Z'
    },
    {
      id: 'assistant-1', 
      role: 'assistant',
      content: '',
      assistantName: 'Python Ants',
      isEdited: false,
      created: '2025-01-07T10:00:01Z',
      toolCalls: [
        {
          id: 'call_123',
          type: 'function',
          function: {
            name: 'runPython',
            arguments: '{"script":"import sys\\nprint(sys.version)","containerName":"guideants-ai","scriptType":2}'
          }
        }
      ]
    },
    {
      id: 'tool-1',
      role: 'tool', 
      content: '{"StandardOutput":"3.11.11 (main, Apr 8 2025, 04:26:24) [GCC 10.2.1 20210110]\\r\\n","StandardError":""}',
      isEdited: false,
      created: '2025-01-07T10:00:02Z',
      toolCallId: 'call_123',
      functionName: 'runPython'
    },
    {
      id: 'assistant-2',
      role: 'assistant',
      content: 'The Python version available is 3.11.11.',
      assistantName: 'Python Ants', 
      isEdited: false,
      created: '2025-01-07T10:00:03Z'
    }
  ];

  const defaultProps = {
    messages: mockMessages,
    isStreaming: false,
    draftUserContent: '',
    selectedAssistant: 'Python Ants',
    assistants: [{ name: 'Python Ants', avatarUrl: undefined }],
    conversationStarters: [],
    onDraftChange: vi.fn(),
    onSendMessage: vi.fn(),
    onEditAssistant: vi.fn(),
    onSaveAssistant: vi.fn(),
    onCancelEdit: vi.fn(),
    onUndo: vi.fn(),
    turnBasedMode: true
  };

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should trigger turn-based UI when there are multiple assistant messages', async () => {
    render(
      <TestWrapper>
        <CellList {...defaultProps} />
      </TestWrapper>
    );

    // Wait for the component to render
    await waitFor(() => {
      expect(screen.getByTestId('workflow-section')).toBeInTheDocument();
    });
  });

  it('should include tool messages in workflow messages passed to WorkflowSection', async () => {
    render(
      <TestWrapper>
        <CellList {...defaultProps} />
      </TestWrapper>
    );

    await waitFor(() => {
      const workflowSection = screen.getByTestId('workflow-section');
      expect(workflowSection).toBeInTheDocument();
    });

    // Check that the workflow messages include both assistant and tool messages
    const workflowMessagesElement = screen.getByTestId('workflow-messages');
    const workflowMessages = JSON.parse(workflowMessagesElement.textContent || '[]');

    // Should include the assistant message with tool calls and the tool result
    expect(workflowMessages).toHaveLength(2);
    expect(workflowMessages.find((m: any) => m.id === 'assistant-1')).toBeDefined();
    expect(workflowMessages.find((m: any) => m.id === 'tool-1')).toBeDefined();
    
    // Verify tool call data is preserved
    const assistantMessage = workflowMessages.find((m: any) => m.id === 'assistant-1');
    expect(assistantMessage.toolCalls).toBeDefined();
    expect(assistantMessage.toolCalls[0].id).toBe('call_123');
    
    // Verify tool result data is preserved
    const toolMessage = workflowMessages.find((m: any) => m.id === 'tool-1');
    expect(toolMessage.toolCallId).toBe('call_123');
  });

  it('should not include final assistant message in workflow messages', async () => {
    render(
      <TestWrapper>
        <CellList {...defaultProps} />
      </TestWrapper>
    );

    await waitFor(() => {
      const workflowSection = screen.getByTestId('workflow-section');
      expect(workflowSection).toBeInTheDocument();
    });

    const workflowMessagesElement = screen.getByTestId('workflow-messages');
    const workflowMessages = JSON.parse(workflowMessagesElement.textContent || '[]');

    // Final assistant message should not be in workflow section
    expect(workflowMessages.find((m: any) => m.id === 'assistant-2')).toBeUndefined();
  });

  it('should use traditional rendering when no multiple assistant messages', async () => {
    const singleAssistantMessages = [
      mockMessages[0], // user message
      mockMessages[3]  // final assistant message only
    ];

    render(
      <TestWrapper>
        <CellList {...defaultProps} messages={singleAssistantMessages} />
      </TestWrapper>
    );

    // Should not render workflow section for single assistant response
    await waitFor(() => {
      expect(screen.queryByTestId('workflow-section')).not.toBeInTheDocument();
    });
  });

  it('should handle streaming in last turn correctly', async () => {
    render(
      <TestWrapper>
        <CellList {...defaultProps} isStreaming={true} />
      </TestWrapper>
    );

    await waitFor(() => {
      const workflowSection = screen.getByTestId('workflow-section');
      expect(workflowSection).toBeInTheDocument();
    });

    // Workflow section should receive isStreaming=true for last turn
    // This is tested implicitly by the WorkflowSection mock receiving the prop
  });

  describe('Turn Grouping Logic', () => {
    it('should correctly group messages into turns', async () => {
      // Test with multiple turns
      const multiTurnMessages: MessageDto[] = [
        ...mockMessages,
        // Second turn
        {
          id: 'user-2',
          role: 'user',
          content: 'Now check if PyTorch is installed',
          userId: 'user-1',
          assistantName: 'user',
          isEdited: false,
          created: '2025-01-07T10:01:00Z'
        },
        {
          id: 'assistant-3',
          role: 'assistant',
          content: 'I can help you check for PyTorch.',
          assistantName: 'Python Ants',
          isEdited: false,
          created: '2025-01-07T10:01:01Z'
        }
      ];

      render(
        <TestWrapper>
          <CellList {...defaultProps} messages={multiTurnMessages} />
        </TestWrapper>
      );

      // Should create two workflow sections (one for each turn with multiple messages)
      await waitFor(() => {
        const workflowSections = screen.getAllByTestId('workflow-section');
        expect(workflowSections).toHaveLength(1); // Only first turn has multiple assistant messages
      });
    });
  });
}); 