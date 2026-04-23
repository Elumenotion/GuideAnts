export type ChatRole = 'system' | 'user' | 'assistant' | 'tool';

export interface MessageDto {
  id: string;
  role: ChatRole;
  content: string;
  userId?: string;
  assistantName?: string;
  isEdited: boolean;
  lastEditedAt?: string;
  /**
   * The original unedited content of the assistant message.
   * Populated only when isEdited === true so that the client can show the
   * prior version in read-only modal overlays.
   */
  originalContent?: string;
  created: string;
  // Tool call information for structured display
  toolCalls?: ToolCallDto[];
  toolCallId?: string;
  functionName?: string;
  // NEW: Multiple attachments support
  attachments?: AttachedFileDto[];
  messageContentType?: 'Text' | 'FileReference' | 'Mixed';
  // DEPRECATED: Single attachment support (kept for backward compatibility)
  attachedNotebookFileId?: string;
  
  // Turn tracking - index of the turn this message belongs to
  turnIndex?: number;
  // Files created during this turn (CWD-relative paths) - only on last assistant message per turn
  turnFilesCreated?: string[];
  // Files modified during this turn (CWD-relative paths) - only on last assistant message per turn
  turnFilesModified?: string[];
}

export interface AttachedFileDto {
  notebookFileId: string;
  fileName: string;
  fileType: 'image' | 'audio' | 'text' | 'other';
  fileSize: number;
  previewUrl?: string;
  type: 'Referenced' | 'Created' | 'Modified';
}

export const isFileReference = (m: MessageDto) => m.role === 'user' && m.messageContentType === 'FileReference';

export interface ToolCallDto {
  id: string;
  type: string;
  function: {
    name: string;
    arguments: string;
  };
}

export interface ConversationState {
  messages: MessageDto[];
  isStreaming: boolean;
  selectedAssistant: string | null;
  draftUserContent: string;
  editingAssistantId?: string;
}

export interface AssistantDefinition {
  id: string;
  name: string;
  description?: string;
  avatarUrl: string;
  modelDeploymentId?: string;
  model?: string;
}

// --- Turn-Based UI Types ---

export type MessageType = 'user_input' | 'thinking' | 'tool_call' | 'tool_result' | 'final_response';

export interface ConversationMessage {
  id: string;
  turnIndex: number;
  messageSequence: number;
  role: ChatRole;
  content: string;
  messageType: MessageType;
  attachedFileId?: string;
  assistantName?: string;
  timestamp: Date;
  isEdited?: boolean;
  lastEditedAt?: Date;
  originalContent?: string;
}

export interface ToolCall {
  id: string;
  name: string;
  parameters: Record<string, any>;
  type: 'code' | 'json' | 'file_operation' | 'other';
}

export interface ToolResult {
  toolCallId: string;
  content: string;
  success: boolean;
  executionTime: number;
  error?: string;
}

export interface AttachedFile {
  id: string;
  notebookFileId: string;
  fileName: string;
  fileType: 'image' | 'audio' | 'text' | 'other';
  fileSize: number;
  processedContent?: string; // base64 or embedded content
  uploadedAt: Date;
  status?: 'uploading' | 'processing' | 'complete' | 'error';
}

export interface TokenUsage {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
}

export interface ConversationTurn {
  turnIndex: number;
  assistantName: string;
  messages: ConversationMessage[];
  toolCalls: ToolCall[];
  toolResults: ToolResult[];
  attachedFiles: AttachedFile[];
  usage: TokenUsage;
  timestamp: Date;
  isStreaming?: boolean;
}

export interface ConversationWithTurns {
  id: string;
  title: string;
  notebookId: string;
  turns: ConversationTurn[];
  createdAt: Date;
  updatedAt: Date;
  status: 'active' | 'archived' | 'deleted';
}

// --- Turn-Based Streaming Types ---

export interface StreamingMessage {
  role: ChatRole;
  content: string;
  timestamp: Date;
  toolCalls?: ToolCallDto[];
}

export interface StreamingToolCall {
  id: string;
  name: string;
  arguments: string;
  status: 'pending' | 'executing' | 'completed' | 'error';
  result?: StreamingToolResult;
  timestamp: Date;
}

export interface StreamingToolResult {
  toolCallId: string;
  content: string;
  isError: boolean;
  timestamp: Date;
}

export interface AssistantStepSection {
  content: string;
  toolCalls: StreamingToolCall[];
  isVisible: boolean;
}

export interface AssistantStepChunk {
  content: string;
  timestamp: Date;
}

export interface StreamingTurn {
  id: string;
  userMessage?: StreamingMessage;
  assistantStepSection?: AssistantStepSection; // legacy single section (first step)
  assistantStepChunks?: AssistantStepChunk[];  // ordered list of assistant steps in arrival order
  toolCalls: StreamingToolCall[];
  toolResults: StreamingToolResult[];
  finalResponse?: StreamingMessage;
  startTime: Date;
  isComplete: boolean;
  pendingCellClear?: boolean; // When true, next token should clear cell first (for phase transitions)
}

export interface StreamingProgress {
  currentPhase: 'thinking' | 'tool_execution' | 'final_response' | 'complete';
  completedSteps: number;
  totalSteps: number;
}

// Enhanced conversation state for turn-based streaming
export interface EnhancedConversationState extends ConversationState {
  // Turn-based streaming state
  currentTurn?: StreamingTurn;
  streamingProgress: StreamingProgress;
  
  // Enhanced streaming flags
  isStreamingToolCalls: boolean;
  isStreamingThinking: boolean;
  
  // Single source of truth for streaming mode
  streamingMode?: 'at-rest' | 'sending' | 'observing';
  activeStreamingUser?: { userId: string; userName: string };
}

// --- Turn-Based State Management ---

export interface TurnBasedConversationState extends ConversationState {
  turns: ConversationTurn[];
  currentTurn: ConversationTurn | null;
  turnBasedMode: boolean; // flag to enable/disable turn-based UI
} 

export interface PendingAttachment {
  notebookFileId: string;
  fileName: string;
  uploadType: 'image' | 'audio' | 'text' | 'other';
}

// User conversations across all projects
export interface UserConversationDto {
  id: string;
  title: string;
  notebookId: string;
  notebookTitle: string;
  projectId: string;
  projectTitle: string;
  created: string;
  lastActivity?: string;
}

export interface PagedUserConversationsDto {
  items: UserConversationDto[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface UserConversationsQuery {
  page?: number;
  pageSize?: number;
  search?: string;
  sortBy?: 'date' | 'project' | 'notebook';
  sortOrder?: 'asc' | 'desc';
} 