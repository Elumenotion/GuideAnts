import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
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

const mockUploadFiles = vi.fn();
const mockNotebookContext = {
  folderTree: null,
  uploadFiles: mockUploadFiles,
  notebookFiles: [] as unknown[],
  notebook: { title: 'Test Notebook' },
  projectId: 'project-1',
  notebookId: 'notebook-1',
};

// Mock the NotebookContext
vi.mock('../../../../contexts/NotebookContext', () => ({
  useNotebook: () => mockNotebookContext,
}));

vi.mock('../../dialogs/SaveAssistantContentDialog', () => ({
  SaveAssistantContentDialog: ({
    isOpen,
    onClose,
    onSave,
  }: {
    isOpen: boolean;
    onClose: () => void;
    onSave: (fileName: string) => Promise<void>;
  }) =>
    isOpen ? (
      <div data-testid="save-dialog">
        <button type="button" onClick={() => void onSave('saved-tool.md')}>
          Confirm save
        </button>
        <button type="button" onClick={onClose}>
          Close save
        </button>
      </div>
    ) : null,
}));

const renderWithProviders = (component: React.ReactElement) => {
  return render(
    <ToastProvider>
      {component}
    </ToastProvider>
  );
};

describe('WorkflowSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockConversationContext.currentTurn = undefined;
    mockConversationContext.isStreamingThinking = false;
    mockConversationContext.isStreamingToolCalls = false;
    mockConversationContext.streamingProgress = { currentPhase: 'complete', completedSteps: 0, totalSteps: 0 };
    mockUploadFiles.mockResolvedValue(undefined);
  });

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
    expect(screen.getByText('Run python')).toBeInTheDocument();
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
    const toolCallButton = screen.getByText('Run python').closest('button');
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
      expect(screen.getByText('Run python')).toBeInTheDocument();
      
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

  it('toggles workflow visibility with Hide and Show buttons', () => {
    const messages = [mockToolCallMessage, mockToolResultMessage];
    renderWithProviders(<WorkflowSection messages={messages} />);

    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));
    expect(screen.getByText('Run python')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Hide workflow/ }));
    expect(screen.queryByText('Parameters:')).not.toBeInTheDocument();
  });

  it('shows parallel tool execution label', () => {
    const parallelToolCallMessage: MessageDto = {
      id: 'assistant-parallel',
      role: 'assistant',
      content: '',
      isEdited: false,
      created: '2025-01-07T10:00:00Z',
      toolCalls: [
        { id: 'call_1', type: 'function', function: { name: 'tool1', arguments: '{}' } },
        { id: 'call_2', type: 'function', function: { name: 'tool2', arguments: '{}' } },
      ],
    };

    renderWithProviders(<WorkflowSection messages={[parallelToolCallMessage]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));

    expect(screen.getByText(/Tool Execution \(2 parallel\)/)).toBeInTheDocument();
  });

  it('shows millisecond duration for fast tool calls', () => {
    const fastCall: MessageDto = {
      ...mockToolCallMessage,
      created: '2025-01-07T10:00:00.000Z',
    };
    const fastResult: MessageDto = {
      ...mockToolResultMessage,
      created: '2025-01-07T10:00:00.200Z',
    };

    renderWithProviders(<WorkflowSection messages={[fastCall, fastResult]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));

    expect(screen.getByText(/200ms/)).toBeInTheDocument();
  });

  it('renders invalid JSON tool arguments as raw text', () => {
    const badArgsMessage: MessageDto = {
      id: 'assistant-bad-json',
      role: 'assistant',
      content: '',
      isEdited: false,
      created: '2025-01-07T10:00:00Z',
      toolCalls: [{
        id: 'call_bad',
        type: 'function',
        function: { name: 'brokenTool', arguments: 'not-json' },
      }],
    };
    const badResult: MessageDto = {
      id: 'tool-bad',
      role: 'tool',
      content: 'done',
      isEdited: false,
      created: '2025-01-07T10:00:01Z',
      toolCallId: 'call_bad',
    };

    renderWithProviders(<WorkflowSection messages={[badArgsMessage, badResult]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));
    fireEvent.click(screen.getByText('Broken tool').closest('button')!);

    expect(screen.getByText('not-json')).toBeInTheDocument();
  });

  it('copies completed tool result to clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      writable: true,
      configurable: true,
    });

    renderWithProviders(<WorkflowSection messages={[mockToolCallMessage, mockToolResultMessage]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));
    fireEvent.click(screen.getByLabelText('Copy tool call'));

    await waitFor(() => {
      expect(writeText).toHaveBeenCalled();
      expect(screen.getByText('Tool call copied to clipboard')).toBeInTheDocument();
    });
  });

  it('opens save dialog and uploads tool markdown', async () => {
    renderWithProviders(<WorkflowSection messages={[mockToolCallMessage, mockToolResultMessage]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));
    fireEvent.click(screen.getByLabelText('Save tool call'));

    expect(screen.getByTestId('save-dialog')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Confirm save'));

    await waitFor(() => {
      expect(mockUploadFiles).toHaveBeenCalled();
    });
  });

  it('dispatches refresh-notebook-files when tool group completes', async () => {
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent');

    renderWithProviders(<WorkflowSection messages={[mockToolCallMessage, mockToolResultMessage]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));

    await waitFor(() => {
      expect(dispatchSpy).toHaveBeenCalledWith(expect.objectContaining({ type: 'refresh-notebook-files' }));
    });

    dispatchSpy.mockRestore();
  });

  it('dedupes consecutive assistant thinking steps', () => {
    const thinkingA: MessageDto = {
      id: 'assistant-a',
      role: 'assistant',
      content: 'Let me think',
      isEdited: false,
      created: '2025-01-07T10:00:00Z',
    };
    const thinkingB: MessageDto = {
      id: 'assistant-b',
      role: 'assistant',
      content: 'Let me think more deeply',
      isEdited: false,
      created: '2025-01-07T10:00:01Z',
    };

    renderWithProviders(<WorkflowSection messages={[thinkingA, thinkingB]} />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));

    expect(screen.getAllByText('Assistant Thinking')).toHaveLength(1);
    expect(screen.getByTestId('markdown-content')).toHaveTextContent('Let me think more deeply');
  });

  it('shows default streaming phase label', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-default',
      toolCalls: [],
      toolResults: [],
      startTime: new Date('2025-01-07T10:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.streamingProgress = {
      currentPhase: 'unknown-phase',
      completedSteps: 0,
      totalSteps: 1,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);
    expect(screen.getByText('Assistant working...')).toBeInTheDocument();
  });

  it('shows streaming indicators when expanded during tool execution', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-tools',
      toolCalls: [{
        id: 'call_stream',
        name: 'runPython',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2025-01-07T10:00:00Z'),
      }],
      toolResults: [],
      startTime: new Date('2025-01-07T10:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.isStreamingToolCalls = true;
    mockConversationContext.streamingProgress = {
      currentPhase: 'tool_execution',
      completedSteps: 0,
      totalSteps: 2,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);
    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));

    expect(screen.getByText(/Assistant is working hard/i)).toBeInTheDocument();
    expect(screen.getByText(/Processing your request with care/i)).toBeInTheDocument();
  });

  it('shows the latest running tool name in the collapsed working slot', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-latest-tool-collapsed',
      toolCalls: [{
        id: 'call_old',
        name: 'DraftOutline',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      }, {
        id: 'call_new',
        name: 'generate_image',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      }],
      toolResults: [],
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.isStreamingToolCalls = true;
    mockConversationContext.streamingProgress = {
      currentPhase: 'tool_execution',
      completedSteps: 0,
      totalSteps: 2,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);

    expect(screen.getByText('Generate image')).toBeInTheDocument();
    expect(screen.queryByText('working...')).not.toBeInTheDocument();
  });

  it('shows the latest running tool name in the expanded running slot', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-latest-tool-expanded',
      toolCalls: [{
        id: 'call_old_expanded',
        name: 'DraftOutline',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      }, {
        id: 'call_new_expanded',
        name: 'Search',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      }],
      toolResults: [],
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.isStreamingToolCalls = true;
    mockConversationContext.streamingProgress = {
      currentPhase: 'tool_execution',
      completedSteps: 0,
      totalSteps: 2,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);

    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));
    expect(screen.getAllByText('Search').length).toBeGreaterThan(0);
    expect(screen.queryByText('running...')).not.toBeInTheDocument();
  });

  it('shows formatted crew and nested tool activity in the collapsed working slot', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-active-tool-collapsed',
      toolCalls: [{
        id: 'call_search',
        name: 'Search',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      }],
      toolResults: [],
      activeCrewActivity: {
        name: 'Search',
        status: 'running',
        source: 'agent_invocation',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      },
      activeToolActivity: {
        name: 'ReadWeb',
        status: 'running',
        toolCallId: 'call_readweb',
        source: 'read_web',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      },
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.isStreamingToolCalls = true;
    mockConversationContext.streamingProgress = {
      currentPhase: 'tool_execution',
      completedSteps: 0,
      totalSteps: 2,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);

    expect(screen.getByText('Search: Read web')).toBeInTheDocument();
    expect(screen.queryByText('working...')).not.toBeInTheDocument();
  });

  it('shows only the formatted crew member in the collapsed slot before a nested tool starts', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-active-crew-collapsed',
      toolCalls: [{
        id: 'call_search_crew_only',
        name: 'Search',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      }],
      toolResults: [],
      activeCrewActivity: {
        name: 'Search',
        status: 'running',
        source: 'agent_invocation',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      },
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.isStreamingToolCalls = true;
    mockConversationContext.streamingProgress = {
      currentPhase: 'tool_execution',
      completedSteps: 0,
      totalSteps: 2,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);

    expect(screen.getByText('Search')).toBeInTheDocument();
    expect(screen.queryByText(/Search:/)).not.toBeInTheDocument();
    expect(screen.queryByText('working...')).not.toBeInTheDocument();
  });

  it('prefers formatted live nested tool activity in the expanded running slot', () => {
    const streamingTurn: StreamingTurn = {
      id: 'streaming-active-tool-expanded',
      toolCalls: [{
        id: 'call_search_expanded',
        name: 'Search',
        arguments: '{}',
        status: 'executing',
        timestamp: new Date('2026-01-01T00:00:00Z'),
      }],
      toolResults: [],
      activeToolActivity: {
        name: 'ReadWeb',
        status: 'running',
        toolCallId: 'call_readweb_expanded',
        source: 'read_web',
        timestamp: new Date('2026-01-01T00:00:01Z'),
      },
      startTime: new Date('2026-01-01T00:00:00Z'),
      isComplete: false,
    };

    mockConversationContext.currentTurn = streamingTurn;
    mockConversationContext.isStreamingToolCalls = true;
    mockConversationContext.streamingProgress = {
      currentPhase: 'tool_execution',
      completedSteps: 0,
      totalSteps: 2,
    };

    renderWithProviders(<WorkflowSection messages={[]} isStreaming />);

    fireEvent.click(screen.getByRole('button', { name: /Show workflow/ }));
    expect(screen.getAllByText('Read web').length).toBeGreaterThan(0);
    expect(screen.queryByText('running...')).not.toBeInTheDocument();
  });
}); 
