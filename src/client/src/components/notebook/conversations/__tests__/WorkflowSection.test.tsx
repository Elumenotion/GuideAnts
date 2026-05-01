import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import WorkflowSection from '../WorkflowSection';
import { MessageDto, StreamingTurn } from '../../../../types/conversation';
import { ToastProvider } from '../../../common/Toast';

// Mock ChatMarkdownViewer since it's not the focus of these tests
vi.mock('../ChatMarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-content">{text}</div>
}));

// Mock the ConversationContext
const mockConversationContext = {
  currentTurn: undefined as StreamingTurn | undefined,
  isStreamingThinking: false,
  isStreamingToolCalls: false,
  streamingProgress: { currentPhase: 'complete', completedSteps: 0, totalSteps: 0 }
};

vi.mock('../../../../contexts/ConversationContext', () => ({
  useConversation: () => mockConversationContext,
  reducer: vi.fn()
}));

// Mock the NotebookContext
vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => ({
    folderTree: null,
    uploadFiles: vi.fn(),
    notebookFiles: []
  })
}));

const renderWithProviders = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

describe('WorkflowSection', () => {
  const mockToolCallMessage: MessageDto = {
    id: 'assistant-1',
    role: 'assistant',
    content: '',
    assistantName: 'Python Ants',
    isEdited: false,
    created: '2025-01-07T10:00:00Z',
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
  };

  const mockToolResultMessage: MessageDto = {
    id: 'tool-1', 
    role: 'tool',
    content: '{"StandardOutput":"3.11.11 (main, Apr 8 2025, 04:26:24) [GCC 10.2.1 20210110]\\r\\n","StandardError":""}',
    isEdited: false,
    created: '2025-01-07T10:00:01Z',
    toolCallId: 'call_123',
    functionName: 'runPython'
  };

  const mockFinalAssistantMessage: MessageDto = {
    id: 'assistant-2',
    role: 'assistant', 
    content: 'The Python version available is 3.11.11.',
    assistantName: 'Python Ants',
    isEdited: false,
    created: '2025-01-07T10:00:02Z'
  };

  it('should render with tool call and result messages', () => {
    const messages = [mockToolCallMessage, mockToolResultMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // Should show the workflow section
    expect(screen.getByText(/Assistant workflow/)).toBeInTheDocument();
    expect(screen.getByText(/\(1 step\)/)).toBeInTheDocument();
  });

  it('should show tool execution group when assistant message has tool calls', () => {
    const messages = [mockToolCallMessage, mockToolResultMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // Need to expand to see the tool details
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    // Should show tool execution
    expect(screen.getByText(/Tool Execution/)).toBeInTheDocument();
    expect(screen.getByText('runPython')).toBeInTheDocument();
  });

  it('should pair tool calls with their results correctly', () => {
    const messages = [mockToolCallMessage, mockToolResultMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // Need to expand to see the status details
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    // Tool call should show completed status since result is present - use more specific selector
    expect(screen.getAllByText('completed')[0]).toBeInTheDocument();
  });

  it('should show expandable tool call details', () => {
    const messages = [mockToolCallMessage, mockToolResultMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // First expand the workflow
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    // Click to expand tool call details
    const toolCallButton = screen.getByText('runPython').closest('button');
    expect(toolCallButton).toBeTruthy();
    fireEvent.click(toolCallButton!);
    
    // Should show parameters and result
    expect(screen.getByText('Parameters:')).toBeInTheDocument();
    expect(screen.getByText('Result:')).toBeInTheDocument();
  });

  it('should handle streaming state correctly', () => {
    const messages: MessageDto[] = [
      {
        id: 'msg-1',
        role: 'assistant',
        content: 'I need to run some code.',
        created: new Date().toISOString(),
        isEdited: false,
        toolCalls: [{
          id: 'call-1',
          type: 'function',
          function: {
            name: 'runPython',
            arguments: '{"code": "print(\'Hello\')"}',
          },
        }],
      },
    ];

    renderWithProviders(<WorkflowSection messages={messages} isStreaming={true} />);

    expect(screen.getByText('Assistant workflow')).toBeInTheDocument();
    
    // Expand to see the streaming status
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    expect(screen.getByText('running')).toBeInTheDocument();
  });

  it('should handle assistant content messages', () => {
    const assistantContentMessage: MessageDto = {
      id: 'assistant-thinking',
      role: 'assistant',
      content: 'Let me check the Python version for you.',
      assistantName: 'Python Ants',
      isEdited: false,
      created: '2025-01-07T10:00:00Z'
    };

    const messages = [assistantContentMessage, mockToolCallMessage, mockToolResultMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // Need to expand the workflow to see the assistant thinking step
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    expect(screen.getByText('Assistant Thinking')).toBeInTheDocument();
    expect(screen.getByTestId('markdown-content')).toHaveTextContent('Let me check the Python version for you.');
  });

  it('should not render when no messages provided', () => {
    const { container } = renderWithProviders(<WorkflowSection messages={[]} />);
    expect(container.firstChild).toBeNull();
  });

  it('should handle parallel tool calls', () => {
    const parallelToolCallMessage: MessageDto = {
      id: 'assistant-parallel',
      role: 'assistant',
      content: '',
      assistantName: 'Python Ants', 
      isEdited: false,
      created: '2025-01-07T10:00:00Z',
      toolCalls: [
        {
          id: 'call_1',
          type: 'function',
          function: { name: 'tool1', arguments: '{}' }
        },
        {
          id: 'call_2', 
          type: 'function',
          function: { name: 'tool2', arguments: '{}' }
        }
      ]
    };

    const messages = [parallelToolCallMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // Need to expand to see the parallel tool details
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    expect(screen.getByText(/Tool Execution/)).toBeInTheDocument();
    expect(screen.getByText('tool1')).toBeInTheDocument();
    expect(screen.getByText('tool2')).toBeInTheDocument();
  });

  it('should calculate tool execution duration correctly', () => {
    const messages = [mockToolCallMessage, mockToolResultMessage];
    
    renderWithProviders(<WorkflowSection messages={messages} />);
    
    // First expand the workflow
    const showButton = screen.getByRole('button', { name: /Show workflow/ });
    fireEvent.click(showButton);
    
    // Should show duration (1 second difference between messages) - it's visible in the tool call button
    expect(screen.getByText(/1.0s/)).toBeInTheDocument();
  });

  describe('processWorkflowSteps function', () => {
    it('should group tool calls with results correctly', () => {
      const messages = [mockToolCallMessage, mockToolResultMessage];
      
      renderWithProviders(<WorkflowSection messages={messages} />);
      
      // Need to expand the workflow section to see the detailed status
      const showButton = screen.getByRole('button', { name: /Show workflow/ });
      fireEvent.click(showButton);
      
      // Verify the tool call is paired with its result by checking completed status - use more specific selector
      const completedElements = screen.getAllByText('completed');
      expect(completedElements.length).toBeGreaterThan(0);
    });

    it('should handle missing tool results', () => {
      const messages = [mockToolCallMessage]; // No result
      
      renderWithProviders(<WorkflowSection messages={messages} />);
      
      // Need to expand the workflow section to see the detailed status
      const showButton = screen.getByRole('button', { name: /Show workflow/ });
      fireEvent.click(showButton);
      
      // Should show pending status when no result
      expect(screen.getByText('pending')).toBeInTheDocument();
    });
  });
  
  // Enhanced streaming functionality tests
  describe('Enhanced Streaming Functionality', () => {
    it('should show thinking phase when assistant is thinking', () => {
      const streamingTurn: StreamingTurn = {
        id: 'streaming-turn-2',
        assistantStepSection: {
          content: 'Let me think about this...',
          toolCalls: [],
          isVisible: true
        },
        toolCalls: [],
        toolResults: [],
        startTime: new Date('2025-01-07T10:00:00Z'),
        isComplete: false
      };
      
      mockConversationContext.currentTurn = streamingTurn;
      mockConversationContext.isStreamingThinking = true;
      mockConversationContext.streamingProgress = {
        currentPhase: 'thinking',
        completedSteps: 0,
        totalSteps: 2
      };
      
      renderWithProviders(<WorkflowSection messages={[]} isStreaming={true} />);
      
      // Should show thinking phase
      expect(screen.getByText('Assistant thinking...')).toBeInTheDocument();
      
      // Need to expand to see the thinking content
      const showButton = screen.getByRole('button', { name: /Show workflow/ });
      fireEvent.click(showButton);
      
      // Should show thinking content - this is rendered through ChatMarkdownViewer which is mocked
      expect(screen.getByTestId('markdown-content')).toHaveTextContent('Let me think about this...');
      
      // Reset mock
      mockConversationContext.currentTurn = undefined;
      mockConversationContext.isStreamingThinking = false;
      mockConversationContext.streamingProgress = { currentPhase: 'complete', completedSteps: 0, totalSteps: 0 };
    });

    it('should handle tool execution completion during streaming', () => {
      const streamingTurn: StreamingTurn = {
        id: 'streaming-turn-3',
        assistantStepSection: {
          content: 'Running Python code...',
          toolCalls: [{
            id: 'call_123',
            name: 'runPython',
            arguments: '{"script":"print(\'Hello\')"}',
            status: 'completed',
            timestamp: new Date('2025-01-07T10:00:00Z')
          }],
          isVisible: true
        },
        toolCalls: [{
          id: 'call_123',
          name: 'runPython',
          arguments: '{"script":"print(\'Hello\')"}',
          status: 'completed',
          timestamp: new Date('2025-01-07T10:00:00Z')
        }],
        toolResults: [{
          toolCallId: 'call_123',
          content: 'Hello',
          isError: false,
          timestamp: new Date('2025-01-07T10:00:01Z')
        }],
        startTime: new Date('2025-01-07T10:00:00Z'),
        isComplete: false
      };
      
      mockConversationContext.currentTurn = streamingTurn;
      mockConversationContext.isStreamingToolCalls = false;
      mockConversationContext.streamingProgress = {
        currentPhase: 'final_response',
        completedSteps: 2,
        totalSteps: 3
      };
      
      renderWithProviders(<WorkflowSection messages={[]} isStreaming={true} />);
      
      // Should show final response phase
      expect(screen.getByText('Generating response...')).toBeInTheDocument();
      
      // Need to expand the workflow section to see the tool details
      const showButton = screen.getByRole('button', { name: /Show workflow/ });
      fireEvent.click(showButton);
      
      // Should show completed tool
      expect(screen.getByText('runPython')).toBeInTheDocument();
      
      // Reset mock
      mockConversationContext.currentTurn = undefined;
      mockConversationContext.streamingProgress = { currentPhase: 'complete', completedSteps: 0, totalSteps: 0 };
    });

    it('should order assistant step before tool execution', () => {
      const streamingTurn: StreamingTurn = {
        id: 'streaming-turn-order',
        assistantStepChunks: [{
          content: 'Step before tools',
          timestamp: new Date('2025-01-07T10:00:00Z')
        }],
        toolCalls: [{
          id: 'call_order',
          name: 'runPython',
          arguments: '{"script":"print(\'Order\')"}',
          status: 'executing',
          timestamp: new Date('2025-01-07T10:00:01Z')
        }],
        toolResults: [],
        startTime: new Date('2025-01-07T10:00:00Z'),
        isComplete: false
      };

      mockConversationContext.currentTurn = streamingTurn;
      mockConversationContext.isStreamingToolCalls = true;
      mockConversationContext.streamingProgress = {
        currentPhase: 'tool_execution',
        completedSteps: 1,
        totalSteps: 2
      };

      renderWithProviders(<WorkflowSection messages={[]} isStreaming={true} />);

      const showButton = screen.getByRole('button', { name: /Show workflow/ });
      fireEvent.click(showButton);

      const assistantHeading = screen.getByText('Assistant Thinking');
      const toolHeading = screen.getByText(/Tool Execution/);
      const position = assistantHeading.compareDocumentPosition(toolHeading);
      expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

      mockConversationContext.currentTurn = undefined;
      mockConversationContext.isStreamingToolCalls = false;
      mockConversationContext.streamingProgress = { currentPhase: 'complete', completedSteps: 0, totalSteps: 0 };
    });
  });
}); 