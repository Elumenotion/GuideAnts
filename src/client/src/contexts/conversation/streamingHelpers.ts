import type { MessageDto, StreamingTurn, StreamingProgress } from '../../types/conversation';

export const generateTurnId = (): string => {
  return `turn-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
};

export const normalizeStreamingMatch = (text: string): string =>
  text.replace(/\s+/g, ' ').trim();

export const appendAssistantStepChunk = (
  chunks: { content: string; timestamp: Date }[],
  content: string,
  timestamp: Date
): { content: string; timestamp: Date }[] => {
  if (!content.trim()) {
    return chunks;
  }

  const normalizedContent = normalizeStreamingMatch(content);
  const last = chunks.length > 0 ? chunks[chunks.length - 1] : null;
  if (last) {
    const normalizedLast = normalizeStreamingMatch(last.content);
    if (!normalizedContent || normalizedContent === normalizedLast) {
      return chunks;
    }
  }

  return [...chunks, { content, timestamp }];
};

export const convertTurnToMessages = (turn: StreamingTurn): MessageDto[] => {
  const messages: MessageDto[] = [];

  if (turn.toolCalls.length > 0) {
    messages.push({
      id: `msg-${Date.now()}-assistant-tools`,
      role: 'assistant',
      content: turn.assistantStepSection?.content || '',
      created: turn.startTime.toISOString(),
      isEdited: false,
      toolCalls: turn.toolCalls.map(tc => ({
        id: tc.id,
        type: 'function',
        function: {
          name: tc.name,
          arguments: tc.arguments
        }
      }))
    } as MessageDto);
  }

  turn.toolResults.forEach(result => {
    messages.push({
      id: `msg-${Date.now()}-tool-${result.toolCallId}`,
      role: 'tool',
      content: result.content,
      created: result.timestamp.toISOString(),
      isEdited: false,
      toolCallId: result.toolCallId
    } as MessageDto);
  });

  if (turn.finalResponse && turn.finalResponse.content) {
    messages.push({
      id: `msg-${Date.now()}-final`,
      role: turn.finalResponse.role,
      content: turn.finalResponse.content,
      created: turn.finalResponse.timestamp.toISOString(),
      isEdited: false
    } as MessageDto);
  } else if (turn.toolCalls.length === 0 && turn.assistantStepSection?.content) {
    messages.push({
      id: `msg-${Date.now()}-final`,
      role: 'assistant',
      content: turn.assistantStepSection.content,
      created: turn.startTime.toISOString(),
      isEdited: false
    } as MessageDto);
  }

  return messages;
};

export const groupToolCallsByExecution = (toolCalls: any[]): any[][] => {
  if (toolCalls.length === 0) return [];

  const groups: any[][] = [];
  let currentGroup: any[] = [toolCalls[0]];

  for (let i = 1; i < toolCalls.length; i++) {
    const current = toolCalls[i];
    const last = currentGroup[currentGroup.length - 1];

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
};

export const calculateStreamingProgress = (turn: StreamingTurn): StreamingProgress => {
  let totalSteps = 0;
  let completedSteps = 0;
  let currentPhase: 'thinking' | 'tool_execution' | 'final_response' | 'complete' = 'thinking';

  const assistantStepCount = turn.assistantStepChunks ? turn.assistantStepChunks.length : (turn.assistantStepSection && turn.assistantStepSection.content ? 1 : 0);
  totalSteps += assistantStepCount;
  completedSteps += assistantStepCount;

  const toolGroups = groupToolCallsByExecution(turn.toolCalls);
  totalSteps += toolGroups.length;

  toolGroups.forEach(group => {
    const groupCompleted = group.every(tc => tc.status === 'completed' || tc.status === 'error');
    if (groupCompleted) {
      completedSteps += 1;
    }
  });

  if (turn.finalResponse) {
    totalSteps += 1;
    completedSteps += 1;
    currentPhase = 'final_response';
  } else if (turn.toolCalls.length > 0) {
    const allToolsComplete = turn.toolCalls.every(tc => tc.status === 'completed' || tc.status === 'error');
    if (allToolsComplete) {
      currentPhase = 'final_response';
    } else {
      currentPhase = 'tool_execution';
    }
  }

  return {
    currentPhase,
    completedSteps,
    totalSteps: Math.max(totalSteps, 1)
  };
};
