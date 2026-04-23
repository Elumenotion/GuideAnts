import { useState, useEffect } from 'react';
import { FaBrain, FaEye, FaEyeSlash, FaChevronDown, FaChevronRight, FaCog, FaCheck, FaExclamationTriangle, FaClock, FaCopy, FaSave } from 'react-icons/fa';
import { MessageDto, ToolCallDto, StreamingTurn, StreamingToolCall } from '../../../types/conversation';
import { useConversation } from '../../../contexts/ConversationContext';
import { useNotebook } from '../../../contexts/NotebookContext';
import { useToast } from '../../common/Toast';
import { buildToolCallMarkdown, generateToolCallMarkdownFile, AssistantContentMetadata } from '../../../utils/assistantContentFile';
import { SaveAssistantContentDialog } from '../dialogs/SaveAssistantContentDialog';
import ChatMarkdownViewer from './ChatMarkdownViewer';

interface WorkflowSectionProps {
  messages: MessageDto[];
  isStreaming?: boolean;
  className?: string;
}

interface WorkflowStep {
  type: 'assistant_content' | 'tool_execution_group';
  timestamp: Date;
  data: AssistantContentStep | ToolExecutionGroupStep;
}

interface AssistantContentStep {
  content: string;
  isStreaming?: boolean;
  messageId: string;
}

interface ToolExecutionGroupStep {
  toolCalls: ToolCallWithResult[];
  isParallel: boolean;
  startTime: Date;
  endTime?: Date;
}

interface ToolCallWithResult {
  call: ToolCallDto;
  result?: MessageDto;
  status: 'pending' | 'running' | 'completed' | 'error';
  duration?: number;
  callMessage: MessageDto;
}

// Enhanced WorkflowSection that supports both historical messages and real-time streaming
export default function WorkflowSection({ 
  messages, 
  isStreaming = false,
  className = '' 
}: WorkflowSectionProps) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [expandedToolCalls, setExpandedToolCalls] = useState<Set<string>>(new Set());
  
  // Get streaming state from context (optional - may not be available in all contexts)
  let currentTurn: StreamingTurn | undefined, 
      isStreamingThinking: boolean, 
      isStreamingToolCalls: boolean, 
      streamingProgress: { currentPhase: string; completedSteps: number; totalSteps: number };
  
  try {
    const conversationContext = useConversation();
    currentTurn = conversationContext.currentTurn;
    isStreamingThinking = conversationContext.isStreamingThinking;
    isStreamingToolCalls = conversationContext.isStreamingToolCalls;
    streamingProgress = conversationContext.streamingProgress;
  } catch (error) {
    // ConversationContext not available - use fallback values
    currentTurn = undefined;
    isStreamingThinking = false;
    isStreamingToolCalls = false;
    streamingProgress = { currentPhase: 'complete', completedSteps: 0, totalSteps: 0 };
  }
  
  // Determine if we should show streaming or historical workflow
  const shouldShowStreaming = isStreaming && currentTurn;
  
  // Process messages or streaming turn into workflow steps
  const streamingAssistantContent = shouldShowStreaming
    ? messages.find(m => m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-'))?.content
    : undefined;

  const rawWorkflowSteps = shouldShowStreaming 
    ? processStreamingTurn(currentTurn!, streamingAssistantContent)
    : processWorkflowSteps(messages, isStreaming);

  // Dedupe consecutive identical assistant-thinking steps, keeping the latter
  const workflowSteps = dedupeConsecutiveAssistantThinkingSteps(rawWorkflowSteps);

  if (workflowSteps.length === 0 && !shouldShowStreaming) {
    return null;
  }

  const toggleToolCall = (toolCallId: string) => {
    const newExpanded = new Set(expandedToolCalls);
    if (newExpanded.has(toolCallId)) {
      newExpanded.delete(toolCallId);
    } else {
      newExpanded.add(toolCallId);
    }
    setExpandedToolCalls(newExpanded);
  };

  const totalSteps = shouldShowStreaming ? streamingProgress.totalSteps : workflowSteps.length;
  const completedSteps = shouldShowStreaming ? streamingProgress.completedSteps : workflowSteps.filter(step => {
    if (step.type === 'assistant_content') {
      const assistantStep = step.data as AssistantContentStep;
      return !assistantStep.isStreaming;
    } else {
      const toolGroup = step.data as ToolExecutionGroupStep;
      return toolGroup.toolCalls.every(tc => tc.status === 'completed' || tc.status === 'error');
    }
  }).length;
  
  // For display: show actual workflow steps count, ensuring at least 1 during streaming
  const displayStepCount = shouldShowStreaming 
    ? Math.max(workflowSteps.length, 1) 
    : completedSteps;

  // Get current phase display
  const getCurrentPhaseDisplay = () => {
    if (!shouldShowStreaming) return 'Assistant workflow';
    
    console.log('🎨 Workflow getCurrentPhaseDisplay:', {
      shouldShowStreaming,
      currentPhase: streamingProgress.currentPhase,
      isStreaming,
      completedSteps,
      totalSteps
    });
    
    switch (streamingProgress.currentPhase) {
      case 'thinking':
        return 'Assistant thinking...';
      case 'tool_execution':
        return 'Executing tools...';
      case 'final_response':
        return 'Generating response...';
      default:
        return 'Assistant working...';
    }
  };

  return (
    <div className={`workflow-section ${className} ${shouldShowStreaming && streamingProgress.currentPhase !== 'complete' ? 'relative overflow-hidden' : ''} max-w-full`} data-testid="workflow-section">
      {/* Subtle streaming background animation */}
      {shouldShowStreaming && streamingProgress.currentPhase !== 'complete' && (
        <div className="absolute inset-0 bg-gradient-to-r from-blue-50 via-purple-50 to-blue-50 opacity-20 animate-pulse"></div>
      )}
      <div className={`p-4 border-b border-gray-100 relative z-10 ${shouldShowStreaming && streamingProgress.currentPhase !== 'complete' 
        ? 'bg-gradient-to-r from-blue-50 to-purple-50 border-blue-200' 
        : 'bg-gradient-to-r from-blue-50 to-purple-50'}`}>
        <div className="flex items-start space-x-3 min-w-0 max-w-full">
          {/* Icon */}
          <div className={`h-8 w-8 flex-shrink-0 rounded-full flex items-center justify-center border ${shouldShowStreaming 
            ? 'bg-gradient-to-r from-blue-100 to-purple-100 border-blue-300 animate-pulse' 
            : 'bg-blue-100 border-blue-200'}`}>
            {shouldShowStreaming ? (
              <div className="relative">
                <FaBrain className="w-4 h-4 text-yellow-600 animate-pulse" />
                <div className="absolute -top-0.5 -right-0.5 w-2 h-2 bg-green-400 rounded-full animate-bounce"></div>
                <div className="absolute -bottom-0.5 -left-0.5 w-1.5 h-1.5 bg-blue-400 rounded-full animate-ping"></div>
              </div>
            ) : (
              <FaBrain className="w-4 h-4 text-yellow-600" />
            )}
          </div>
          
          <div className="flex-1 min-w-0 overflow-hidden">
            {/* Section Header */}
            <div className="flex items-center justify-between mb-2 flex-wrap gap-1">
              <div className="flex items-center space-x-2">
                <span className="text-sm font-medium text-gray-700">
                  {getCurrentPhaseDisplay()}
                </span>
                <span className="text-xs text-gray-500">
                  ({displayStepCount} step{displayStepCount !== 1 ? 's' : ''})
                </span>
              </div>
              
              {/* Toggle Button */}
              <button
                onClick={() => setIsExpanded(!isExpanded)}
                className="flex items-center space-x-1 text-xs text-gray-500 hover:text-gray-700 focus:outline-none"
                aria-label={isExpanded ? 'Hide workflow' : 'Show workflow'}
              >
                {isExpanded ? (
                  <>
                    <FaEyeSlash className="w-3 h-3" />
                    <span>Hide</span>
                    <FaChevronDown className="w-3 h-3" />
                  </>
                ) : (
                  <>
                    <FaEye className="w-3 h-3" />
                    <span>Show</span>
                    <FaChevronRight className="w-3 h-3" />
                  </>
                )}
              </button>
            </div>

            {/* Workflow Timeline Preview */}
            {!isExpanded && (
              <div className="flex items-center space-x-2 text-xs text-gray-600">
                <div className="flex items-center space-x-1">
                  {workflowSteps.map((step, index) => (
                    <div key={index} className="flex items-center space-x-1">
                      {step.type === 'assistant_content' ? (
                        <FaBrain className="w-3 h-3 text-yellow-500" />
                      ) : (
                        <FaCog className="w-3 h-3 text-purple-500" />
                      )}
                      {index < workflowSteps.length - 1 && (
                        <span className="text-gray-300">→</span>
                      )}
                    </div>
                  ))}
                  
                  {/* Show streaming activity indicator when collapsed */}
                  {shouldShowStreaming && (
                    <>
                      {workflowSteps.length > 0 && <span className="text-gray-300">→</span>}
                      <div className="flex items-center space-x-1">
                        <div className="relative">
                          <FaCog className="w-3 h-3 text-blue-500 animate-spin" />
                          <div className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 bg-green-400 rounded-full animate-bounce"></div>
                        </div>
                        <span className="text-blue-600 animate-pulse">working...</span>
                        <div className="w-1 h-1 bg-yellow-400 rounded-full animate-ping"></div>
                      </div>
                    </>
                  )}
                </div>
              </div>
            )}

            {/* Workflow Steps */}
            {isExpanded && (
              <div className="space-y-4 mt-4">
                {workflowSteps.map((step, stepIndex) => {
                  const stepNumber = stepIndex + 1;
                  return (
                    <div key={stepIndex} className="workflow-step">
                      {step.type === 'assistant_content' ? (
                        <AssistantContentStepComponent 
                          step={step.data as AssistantContentStep}
                          stepNumber={stepNumber}
                        />
                      ) : (
                        <ToolExecutionGroupComponent 
                          step={step.data as ToolExecutionGroupStep}
                          stepNumber={stepNumber}
                          expandedToolCalls={expandedToolCalls}
                          onToggleToolCall={toggleToolCall}
                        />
                      )}
                    </div>
                  );
                })}
                
                {/* Show streaming indicators for in-progress work */}
                {shouldShowStreaming && (
                  <StreamingIndicators 
                    isStreamingThinking={isStreamingThinking}
                    isStreamingToolCalls={isStreamingToolCalls}
                    streamingProgress={streamingProgress}
                  />
                )}
              </div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

// Helper function to process streaming turn into workflow steps
function processStreamingTurn(turn: StreamingTurn, currentAssistantContent?: string): WorkflowStep[] {
  const steps: WorkflowStep[] = [];
  const normalize = (text: string): string => text.replace(/\s+/g, ' ').trim();

  const assistantEvents: Array<{ timestamp: Date; content: string; isStreaming: boolean }> = [];

  if (turn.assistantStepChunks && turn.assistantStepChunks.length > 0) {
    turn.assistantStepChunks.forEach(chunk => {
      assistantEvents.push({ timestamp: chunk.timestamp, content: chunk.content, isStreaming: false });
    });
  } else if (turn.assistantStepSection && turn.assistantStepSection.content) {
    assistantEvents.push({ timestamp: turn.startTime, content: turn.assistantStepSection.content, isStreaming: false });
  }

  if (currentAssistantContent && currentAssistantContent.trim()) {
    const lastChunk = turn.assistantStepChunks && turn.assistantStepChunks.length > 0
      ? turn.assistantStepChunks[turn.assistantStepChunks.length - 1]
      : null;
    const normalizedCurrent = normalize(currentAssistantContent);
    const normalizedLast = lastChunk ? normalize(lastChunk.content) : '';
    if (!normalizedCurrent || normalizedCurrent !== normalizedLast) {
      assistantEvents.push({
        timestamp: new Date(),
        content: currentAssistantContent,
        isStreaming: true
      });
    }
  }

  const toolGroups = groupStreamingToolCallsByExecution(turn.toolCalls);
  const toolEvents = toolGroups.map(group => ({
    timestamp: group[0].timestamp,
    group
  }));

  assistantEvents.sort((a, b) => a.timestamp.getTime() - b.timestamp.getTime());
  toolEvents.sort((a, b) => a.timestamp.getTime() - b.timestamp.getTime());

  let assistantIndex = 0;
  let toolIndex = 0;
  while (assistantIndex < assistantEvents.length || toolIndex < toolEvents.length) {
    const nextAssistant = assistantEvents[assistantIndex];
    const nextTool = toolEvents[toolIndex];

    const takeAssistant =
      nextAssistant &&
      (!nextTool || nextAssistant.timestamp.getTime() <= nextTool.timestamp.getTime());

    if (takeAssistant && nextAssistant) {
      steps.push({
        type: 'assistant_content',
        timestamp: nextAssistant.timestamp,
        data: {
          content: nextAssistant.content,
          isStreaming: nextAssistant.isStreaming,
          messageId: `assistant-step-${nextAssistant.timestamp.getTime()}`
        }
      });
      assistantIndex += 1;
      continue;
    }

    if (nextTool) {
      const toolCallsWithResults: ToolCallWithResult[] = nextTool.group.map(toolCall => {
        const toolResult = turn.toolResults.find(tr => tr.toolCallId === toolCall.id);
        return {
          call: {
            id: toolCall.id,
            type: 'function',
            function: {
              name: toolCall.name,
              arguments: toolCall.arguments
            }
          },
          result: toolResult ? {
            id: `result-${toolCall.id}`,
            role: 'tool',
            content: toolResult.content,
            created: toolResult.timestamp.toISOString(),
            isEdited: false,
            toolCallId: toolCall.id
          } as MessageDto : undefined,
          status: toolCall.status === 'executing' ? 'running' : toolCall.status,
          callMessage: {
            id: `call-${toolCall.id}`,
            role: 'assistant',
            content: '',
            created: toolCall.timestamp.toISOString(),
            isEdited: false,
            toolCalls: [{
              id: toolCall.id,
              type: 'function',
              function: {
                name: toolCall.name,
                arguments: toolCall.arguments
              }
            }]
          } as MessageDto
        };
      });

      const groupEndTime = toolCallsWithResults.every(tc => tc.result)
        ? new Date(Math.max(...toolCallsWithResults.map(tc => new Date(tc.result!.created).getTime())))
        : undefined;

      steps.push({
        type: 'tool_execution_group',
        timestamp: nextTool.timestamp,
        data: {
          toolCalls: toolCallsWithResults,
          isParallel: nextTool.group.length > 1,
          startTime: nextTool.timestamp,
          endTime: groupEndTime
        }
      });
      toolIndex += 1;
    }
  }

  return steps;
}

// Helper function to group streaming tool calls by execution batch
function groupStreamingToolCallsByExecution(toolCalls: StreamingToolCall[]): StreamingToolCall[][] {
  if (toolCalls.length === 0) return [];
  
  const groups: StreamingToolCall[][] = [];
  let currentGroup: StreamingToolCall[] = [toolCalls[0]];
  
  for (let i = 1; i < toolCalls.length; i++) {
    const current = toolCalls[i];
    const last = currentGroup[currentGroup.length - 1];
    
    // If timestamps are close (within 1 second), they're likely in the same batch
    const timeDiff = Math.abs(current.timestamp.getTime() - last.timestamp.getTime());
    if (timeDiff < 1000) {
      currentGroup.push(current);
    } else {
      groups.push(currentGroup);
      currentGroup = [current];
    }
  }
  
  groups.push(currentGroup);
  return groups;
}

// Component to show streaming indicators
function StreamingIndicators({ 
  isStreamingThinking, 
  isStreamingToolCalls, 
  streamingProgress 
}: {
  isStreamingThinking: boolean;
  isStreamingToolCalls: boolean;
  streamingProgress: { currentPhase: string; completedSteps: number; totalSteps: number };
}) {
  const shouldHide =
    streamingProgress.currentPhase === 'complete'
    && !isStreamingThinking
    && !isStreamingToolCalls;
  if (shouldHide) {
    return null;
  }
  
  console.log('🎨 StreamingIndicators rendering:', {
    currentPhase: streamingProgress.currentPhase,
    isStreamingThinking,
    isStreamingToolCalls,
    completedSteps: streamingProgress.completedSteps,
    totalSteps: streamingProgress.totalSteps
  });
  
  return (
    <div className="streaming-indicators bg-gradient-to-r from-blue-50 to-purple-50 p-4 rounded-lg border border-blue-100">
      {/* Simple activity indicator with explicit classes */}
      <div className="flex items-center space-x-3 mb-3">
        <div className="relative">
          <FaCog className="w-5 h-5 text-blue-600 animate-spin" />
          <div className="absolute -top-1 -right-1 w-2 h-2 bg-green-400 rounded-full animate-bounce"></div>
          <div className="absolute -bottom-1 -left-1 w-1.5 h-1.5 bg-yellow-400 rounded-full animate-pulse"></div>
        </div>
        
        <div className="flex-1">
          <div className="text-sm font-medium text-blue-700 animate-pulse">
            Assistant is working hard! 🚀
          </div>
          <div className="text-xs text-gray-600 mt-1">
            Step {streamingProgress.completedSteps + 1}
          </div>
        </div>
        
        {/* Activity dots */}
        <div className="flex space-x-1">
          <div className="w-2 h-2 bg-blue-400 rounded-full animate-bounce"></div>
          <div className="w-2 h-2 bg-purple-400 rounded-full animate-bounce" style={{ animationDelay: '0.1s' }}></div>
          <div className="w-2 h-2 bg-green-400 rounded-full animate-bounce" style={{ animationDelay: '0.2s' }}></div>
        </div>
      </div>
      
      {/* Simple progress bar - currentStep / (currentStep + 1) so it never fills */}
      <div className="relative w-full bg-gray-200 rounded-full h-3 overflow-hidden">
        <div 
          className="bg-gradient-to-r from-blue-500 to-purple-600 h-3 rounded-full transition-all duration-500 relative"
          style={{ width: `${((streamingProgress.completedSteps + 1) / (streamingProgress.completedSteps + 2)) * 100}%` }}
        >
          <div className="absolute inset-0 bg-gradient-to-r from-transparent via-white to-transparent opacity-30 animate-pulse"></div>
        </div>
      </div>
      
      <div className="flex items-center space-x-2 text-xs text-purple-600 mt-2">
        <FaCog className="w-3 h-3 animate-spin" />
        <span className="animate-pulse">Processing your request with care...</span>
      </div>
    </div>
  );
}

// Helper function to process messages into workflow steps
function processWorkflowSteps(messages: MessageDto[], isStreaming: boolean): WorkflowStep[] {
  const steps: WorkflowStep[] = [];
  let i = 0;

  while (i < messages.length) {
    const message = messages[i];

    if (message.role.toLowerCase() === 'assistant') {
      // Always show assistant content if it has text
      if (message.content.trim()) {
        steps.push({
          type: 'assistant_content',
          timestamp: new Date(message.created),
          data: {
            content: message.content,
            isStreaming: isStreaming && i === messages.length - 1,
            messageId: message.id
          }
        });
      }
      
      // Additionally, if this assistant message has tool calls, create tool execution group
      if (message.toolCalls && message.toolCalls.length > 0) {
        const toolCallsWithResults: ToolCallWithResult[] = [];
        
        for (const toolCall of message.toolCalls) {
          // Find the corresponding tool result
          const resultMessage = messages.find(m => 
            m.role.toLowerCase() === 'tool' && m.toolCallId === toolCall.id
          );
          
          const status = resultMessage ? 'completed' : (isStreaming ? 'running' : 'pending');
          
          toolCallsWithResults.push({
            call: toolCall,
            result: resultMessage,
            status,
            callMessage: message,
            duration: resultMessage ? calculateDuration(message.created, resultMessage.created) : undefined
          });
        }

        steps.push({
          type: 'tool_execution_group',
          timestamp: new Date(message.created),
          data: {
            toolCalls: toolCallsWithResults,
            isParallel: message.toolCalls.length > 1,
            startTime: new Date(message.created),
            endTime: toolCallsWithResults.every(tc => tc.result) 
              ? new Date(Math.max(...toolCallsWithResults.map(tc => new Date(tc.result!.created).getTime())))
              : undefined
          }
        });
      }
    }

    i++;
  }

  return steps;
}

function calculateDuration(start: string, end: string): number {
  return new Date(end).getTime() - new Date(start).getTime();
}

// Merge consecutive assistant thinking steps into a single step
function dedupeConsecutiveAssistantThinkingSteps(steps: WorkflowStep[]): WorkflowStep[] {
  const merged: WorkflowStep[] = [];

  const normalize = (text: string): string =>
    text.replace(/\s+/g, ' ').trim();

  const mergeContent = (previous: string, current: string): string => {
    const normalizedPrevious = normalize(previous);
    const normalizedCurrent = normalize(current);
    if (!normalizedPrevious) return current;
    if (!normalizedCurrent) return previous;
    if (normalizedCurrent === normalizedPrevious) return previous;
    if (normalizedCurrent.startsWith(normalizedPrevious)) return current;
    if (normalizedPrevious.startsWith(normalizedCurrent)) return previous;
    if (normalizedPrevious.includes(normalizedCurrent)) return previous;
    if (normalizedCurrent.includes(normalizedPrevious)) return current;
    const needsSeparator = !previous.endsWith('\n') && !current.startsWith('\n');
    return needsSeparator ? `${previous}\n\n${current}` : `${previous}${current}`;
  };

  for (const step of steps) {
    const last = merged[merged.length - 1];
    if (
      last &&
      last.type === 'assistant_content' &&
      step.type === 'assistant_content'
    ) {
      const lastData = last.data as AssistantContentStep;
      const currentData = step.data as AssistantContentStep;
      const combinedContent = mergeContent(lastData.content ?? '', currentData.content ?? '');

      merged[merged.length - 1] = {
        ...step,
        timestamp: step.timestamp,
        data: {
          ...currentData,
          content: combinedContent,
          isStreaming: Boolean(lastData.isStreaming || currentData.isStreaming),
          messageId: currentData.messageId || lastData.messageId
        }
      };
      continue;
    }

    merged.push(step);
  }

  return merged;
}

// Assistant Content Step Component
function AssistantContentStepComponent({ 
  step, 
  stepNumber 
}: { 
  step: AssistantContentStep; 
  stepNumber: number; 
}) {
  const { projectId, notebookId } = useNotebook();

  return (
    <div className={`flex items-start space-x-3 min-w-0 max-w-full ${step.isStreaming ? 'animate-pulse' : ''}`}>
      <div className={`flex-shrink-0 w-6 h-6 rounded-full flex items-center justify-center ${
        step.isStreaming 
          ? 'bg-gradient-to-r from-blue-200 to-purple-200 animate-pulse' 
          : 'bg-blue-200'
      }`}>
        <span className="text-blue-700 text-xs font-medium">
          {stepNumber}
        </span>
        {step.isStreaming && (
          <div className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 bg-green-400 rounded-full animate-bounce"></div>
        )}
      </div>
      <div className="flex-1 min-w-0 overflow-hidden">
        <div className="flex items-center space-x-2 mb-2 flex-wrap">
          <div className="relative flex-shrink-0">
            <FaBrain className={`w-4 h-4 text-yellow-500 ${step.isStreaming ? 'animate-pulse' : ''}`} />
            {step.isStreaming && (
              <div className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 bg-blue-400 rounded-full animate-bounce"></div>
            )}
          </div>
          <span className="text-sm font-medium text-gray-700">Assistant Thinking</span>
          {step.isStreaming && (
            <div className="flex items-center space-x-1">
              <div className="w-2 h-2 bg-blue-500 rounded-full animate-bounce"></div>
              <span className="text-xs text-blue-500 animate-pulse">active...</span>
            </div>
          )}
        </div>
        <div className={`prose prose-sm max-w-none overflow-hidden ${step.isStreaming ? 'border-l-2 border-blue-300 pl-3 animate-pulse' : ''}`}>
          <ChatMarkdownViewer 
            text={step.content} 
            className="text-sm text-gray-700 break-words" 
            isStreaming={step.isStreaming === true}
            maxImageHeight={240}
            projectId={projectId}
            notebookId={notebookId}
          />
        </div>
      </div>
    </div>
  );
}

// Tool Execution Group Component
function ToolExecutionGroupComponent({ 
  step, 
  stepNumber,
  expandedToolCalls,
  onToggleToolCall
}: { 
  step: ToolExecutionGroupStep; 
  stepNumber: number;
  expandedToolCalls: Set<string>;
  onToggleToolCall: (toolCallId: string) => void;
}) {
  const allCompleted = step.toolCalls.every(tc => tc.status === 'completed');
  const hasErrors = step.toolCalls.some(tc => tc.status === 'error');
  const isRunning = step.toolCalls.some(tc => tc.status === 'running');

  // Signal the notebook files refresh one-time when this group completes
  const [refreshSignaled, setRefreshSignaled] = useState(false);
  useEffect(() => {
    if (!refreshSignaled && allCompleted) {
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
      setRefreshSignaled(true);
    }
  }, [allCompleted, refreshSignaled]);

  return (
    <div className={`flex items-start space-x-3 min-w-0 max-w-full ${isRunning ? 'animate-pulse' : ''}`}>
      <div className={`flex-shrink-0 w-6 h-6 rounded-full flex items-center justify-center relative ${
        isRunning 
          ? 'bg-gradient-to-r from-purple-200 to-blue-200 animate-pulse' 
          : allCompleted && !hasErrors
          ? 'bg-green-200'
          : hasErrors
          ? 'bg-red-200'
          : 'bg-purple-200'
      }`}>
        <span className={`text-xs font-medium ${
          isRunning 
            ? 'text-purple-700' 
            : allCompleted && !hasErrors
            ? 'text-green-700'
            : hasErrors
            ? 'text-red-700'
            : 'text-purple-700'
        }`}>
          {stepNumber}
        </span>
        {isRunning && (
          <div className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 bg-yellow-400 rounded-full animate-bounce"></div>
        )}
      </div>
      <div className="flex-1 min-w-0 overflow-hidden">
        <div className="flex items-center space-x-2 mb-3 flex-wrap gap-1">
          <div className="relative flex-shrink-0">
            <FaCog className={`w-4 h-4 text-purple-500 ${isRunning ? 'animate-spin' : ''}`} />
            {isRunning && (
              <div className="absolute -top-0.5 -right-0.5 w-1.5 h-1.5 bg-blue-400 rounded-full animate-bounce"></div>
            )}
          </div>
          <span className="text-sm font-medium text-gray-700">
            Tool Execution {step.isParallel ? `(${step.toolCalls.length} parallel)` : ''}
          </span>
          {isRunning && (
            <div className="flex items-center space-x-1">
              <FaClock className="w-3 h-3 text-yellow-500 animate-spin" />
              <span className="text-xs text-yellow-600 animate-pulse">running...</span>
            </div>
          )}
          {allCompleted && !hasErrors && (
            <div className="flex items-center space-x-1">
              <FaCheck className="w-3 h-3 text-green-500" />
              <span className="text-xs text-green-600">completed</span>
            </div>
          )}
          {hasErrors && (
            <div className="flex items-center space-x-1">
              <FaExclamationTriangle className="w-3 h-3 text-red-500" />
              <span className="text-xs text-red-600">error</span>
            </div>
          )}
        </div>
        
        <div className={`space-y-2 overflow-hidden ${isRunning ? 'border-l-2 border-purple-300 pl-3' : ''}`}>
          {step.toolCalls.map((toolCallWithResult) => (
            <ToolCallCard
              key={toolCallWithResult.call.id}
              toolCallWithResult={toolCallWithResult}
              isExpanded={expandedToolCalls.has(toolCallWithResult.call.id)}
              onToggle={() => onToggleToolCall(toolCallWithResult.call.id)}
            />
          ))}
        </div>
      </div>
    </div>
  );
}

// Individual Tool Call Card
function ToolCallCard({ 
  toolCallWithResult, 
  isExpanded, 
  onToggle 
}: { 
  toolCallWithResult: ToolCallWithResult;
  isExpanded: boolean;
  onToggle: () => void;
}) {
  const { call, result, status, duration } = toolCallWithResult;
  const [showSaveDialog, setShowSaveDialog] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  
  const { showToast } = useToast();
  const { notebook, uploadFiles, projectId, notebookId } = useNotebook();
  
  const statusIcon = {
    pending: <FaClock className="w-3 h-3 text-gray-400" />,
    running: <FaClock className="w-3 h-3 text-yellow-500 animate-spin" />,
    completed: <FaCheck className="w-3 h-3 text-green-500" />,
    error: <FaExclamationTriangle className="w-3 h-3 text-red-500" />
  }[status];

  const statusColor = {
    pending: 'border-gray-200 bg-gray-50',
    running: 'border-yellow-200 bg-yellow-50',
    completed: 'border-green-200 bg-green-50',
    error: 'border-red-200 bg-red-50'
  }[status];

  // Determine if buttons should be visible
  const canCopy = result && result.content && result.content.trim().length > 0;
  const canSave = canCopy && typeof uploadFiles === 'function' && notebook && status === 'completed';

  // Copy to clipboard handler
  const handleCopyToClipboard = async () => {
    if (!result?.content) return;
    
    try {
      const metadata: AssistantContentMetadata = {
        messageId: `tool-${call.id}`,
        conversationId: undefined,
        assistantName: call.function.name,
        notebookTitle: notebook?.title,
        generatedAt: new Date().toISOString(),
        wordCount: result.content.trim().split(/\s+/).length
      };
      
      const markdown = buildToolCallMarkdown({
        toolName: call.function.name,
        parameters: call.function.arguments,
        resultContent: result.content,
        metadata
      });
      
      await navigator.clipboard.writeText(markdown);
      showToast({
        type: 'success',
        title: 'Tool call copied to clipboard',
        duration: 3000
      });
    } catch (error) {
      console.error('Failed to copy to clipboard:', error);
      showToast({
        type: 'error',
        title: 'Copy failed',
        message: 'Unable to copy content. Please try again.',
        duration: 5000
      });
    }
  };

  // Save handler
  const handleSave = () => {
    if (canSave) {
      setShowSaveDialog(true);
    }
  };

  // Handle save from dialog
  const handleSaveFromDialog = async (fileName: string, folderPath?: string) => {
    if (!result?.content || !uploadFiles) {
      throw new Error('Save functionality not available');
    }

    setIsSaving(true);
    try {
      const metadata: AssistantContentMetadata = {
        messageId: `tool-${call.id}`,
        conversationId: undefined,
        assistantName: call.function.name,
        notebookTitle: notebook?.title,
        generatedAt: new Date().toISOString(),
        wordCount: result.content.trim().split(/\s+/).length
      };
      
      const markdownFile = generateToolCallMarkdownFile({
        toolName: call.function.name,
        parameters: call.function.arguments,
        resultContent: result.content,
        metadata
      }, fileName);

      await uploadFiles([markdownFile], folderPath, false);
      // Notify notebook sidebar to immediately refresh files (no state coupling back)
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}
    } catch (error) {
      console.error('Save failed:', error);
      throw error;
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <>
      <div className={`border rounded-lg ${statusColor} transition-all duration-200 overflow-hidden w-full max-w-full relative`}>
        
        <button
          onClick={onToggle}
          className="w-full p-3 text-left hover:bg-black hover:bg-opacity-5 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-inset rounded-lg"
        >
          <div className="flex items-center justify-between gap-2">
            <div className="flex items-center space-x-2 min-w-0 flex-1">
              <span className="flex-shrink-0">{statusIcon}</span>
              <span className="text-sm font-medium text-gray-900 truncate">{call.function.name}</span>
              {duration && (
                <span className="text-xs text-gray-500 flex-shrink-0">
                  ({duration < 1000 ? `${duration}ms` : `${(duration / 1000).toFixed(1)}s`})
                </span>
              )}
            </div>
            <div className="flex items-center space-x-1 md:space-x-2 flex-shrink-0">
              <span className="text-xs text-gray-500 capitalize">{status}</span>
              {/* Action buttons before chevron */}
              {canSave && (
                <button
                  type="button"
                  onClick={(e) => { e.stopPropagation(); handleSave(); }}
                  className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700 disabled:opacity-50"
                  aria-label="Save tool call"
                  title="Save to notebook"
                  disabled={isSaving}
                >
                  <FaSave className="w-3 h-3" />
                </button>
              )}
              {canCopy && (
                <button
                  type="button"
                  onClick={(e) => { e.stopPropagation(); handleCopyToClipboard(); }}
                  className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
                  aria-label="Copy tool call"
                  title="Copy to clipboard"
                >
                  <FaCopy className="w-3 h-3" />
                </button>
              )}
              {/* Chevron is rightmost */}
              {isExpanded ? (
                <FaChevronDown className="w-3 h-3 text-gray-400" />
              ) : (
                <FaChevronRight className="w-3 h-3 text-gray-400" />
              )}
            </div>
          </div>
        </button>
       
        {isExpanded && (
          <div className="px-3 pb-3 border-t border-gray-200 space-y-3">
            {/* Tool Call Parameters */}
            <div className="overflow-hidden">
              <div className="text-xs font-medium text-gray-600 mb-1">Parameters:</div>
              <pre className="text-xs text-gray-700 bg-white p-2 rounded border overflow-x-auto whitespace-pre-wrap break-words max-w-full" style={{ wordBreak: 'break-word' }}>
                {(() => {
                  try {
                    return JSON.stringify(JSON.parse(call.function.arguments), null, 2);
                  } catch {
                    return call.function.arguments;
                  }
                })()}
              </pre>
            </div>
           
            {/* Tool Result */}
            {result && (
              <div className="overflow-hidden">
                <div className="text-xs font-medium text-gray-600 mb-1">Result:</div>
                <div className="text-xs text-gray-700 bg-white p-2 rounded border whitespace-pre-wrap break-words overflow-x-auto max-w-full" style={{ wordBreak: 'break-word' }}>
                  <ChatMarkdownViewer 
                    text={result.content} 
                    className="prose-xs" 
                    isStreaming={status === 'running'} 
                    maxImageHeight={120}
                    projectId={projectId}
                    notebookId={notebookId}
                  />
                </div>
              </div>
            )}
          </div>
        )}
      </div>
      
      {/* Save Dialog */}
      {showSaveDialog && result && (
        <SaveAssistantContentDialog
          isOpen={showSaveDialog}
          onClose={() => setShowSaveDialog(false)}
          onSave={handleSaveFromDialog}
          content={buildToolCallMarkdown({
            toolName: call.function.name,
            parameters: call.function.arguments,
            resultContent: result.content,
            metadata: {
              messageId: `tool-${call.id}`,
              conversationId: undefined,
              assistantName: call.function.name,
              notebookTitle: notebook?.title,
              generatedAt: new Date().toISOString(),
              wordCount: result.content.trim().split(/\s+/).length
            }
          })}
          assistantName={call.function.name}
          notebookTitle={notebook?.title}
          disabled={isSaving}
        />
      )}
    </>
  );
} 