import type { EnhancedConversationState, PendingAttachment, AssistantDefinition } from '../../types/conversation';
import type { UserInfo } from '../../services/userService';
import type { NotebookTemplateDto } from '../../types/project';

export type StreamingMode = 'at-rest' | 'sending' | 'observing';

export interface ConversationContextProps extends EnhancedConversationState {
  sendMessage: (content: string, attachments?: PendingAttachment[]) => Promise<void>;
  editAssistantMessage: (messageId: string, content: string) => Promise<void>;
  undoLastTurn: () => Promise<void>;
  setSelectedAssistant: (name: string) => void;
  setDraftUserContent: (text: string) => void;
  startEditingAssistant: (messageId: string) => void;
  cancelEditingAssistant: () => void;
  refresh: () => Promise<void>;
  assistants: AssistantDefinition[];
  currentAssistant?: { name: string; model?: string; avatarUrl: string; id?: string };
  assistantByName?: Record<string, { name: string; model?: string; avatarUrl: string; id?: string }>;
  conversationStarters: string[];
  editError?: string;
  isEditLoading: boolean;
  isInitialized: boolean;
  isCancelling: boolean;
  streamingError?: string;
  streamingMode: StreamingMode;
  activeStreamingUser?: { userId: string; userName: string };
  pendingAttachments: PendingAttachment[];
  userProfiles?: Record<string, UserInfo>;

  handleStreamingEvent: (event: { type: string; data: any }) => void;
  setStreamingMode: (mode: StreamingMode, activeUser?: { userId: string; userName: string }) => Promise<void>;
  cancelStream: () => void;
  addPendingAttachment: (att: PendingAttachment) => void;
  removePendingAttachment: (fileId: string) => void;
  onPreviewFile?: (fileId: string) => void;
  onPreviewFileByPath?: (relativePath: string) => void;
}

export interface ProviderProps {
  projectId: string;
  notebookId: string;
  conversationId: string;
  guideId?: string;
  assistants?: Array<{ name: string; model?: string; avatarUrl: string; id?: string }>;
  notebookTemplate?: NotebookTemplateDto;
  onPreviewFile?: (fileId: string) => void;
  onPreviewFileByPath?: (relativePath: string) => void;
  children: React.ReactNode;
}

export interface ActionType {
  type: 'SET_MESSAGES' | 'ADD_MESSAGE' | 'UPDATE_MESSAGE' | 'REMOVE_LAST_TURN' | 'SET_STREAMING' | 'SET_ASSISTANT' | 'SET_DRAFT' | 'SET_EDITING' | 'SET_EDIT_ERROR' | 'SET_EDIT_LOADING' | 'APPEND_TOKEN' | 'FINALIZE_STREAMING_MESSAGE' | 'SET_ASSISTANTS' | 'SET_CONVERSATION_STARTERS' | 'SET_INITIALIZED' | 'SET_JUST_COMPLETED_STREAMING' | 'SET_CANCELLING' | 'SET_USER_PROFILES' | 'SET_STREAMING_ERROR' | 'SET_NOTEBOOK_TEMPLATE' |
  'START_STREAMING_TURN' | 'SET_TOOL_CALLS' | 'ENSURE_TOOL_CALL' | 'ADD_TOOL_RESULT' | 'ADD_FINAL_RESPONSE' | 'COMPLETE_STREAMING_TURN' | 'UPDATE_STREAMING_PROGRESS' | 'ADD_TOOL_ERROR' | 'ADD_ATTACHMENT' | 'REMOVE_ATTACHMENT' | 'CLEAR_ATTACHMENTS' | 'CONVERT_STREAMING_IDS' | 'CLEAR_STREAMING_CELL' | 'SET_PENDING_CELL_CLEAR' |
  'SET_STREAMING_MODE';
  payload?: any;
}

export interface ExtendedConversationState extends EnhancedConversationState {
  streamingMode?: StreamingMode;
  activeStreamingUser?: { userId: string; userName: string };
  assistants?: any[];
  conversationStarters?: string[];
  editError?: string;
  isEditLoading?: boolean;
  streamingError?: string;
  _renderCounter?: number;
  _isInitialized?: boolean;
  _justCompletedStreaming?: boolean;
  _isCancelling?: boolean;
  pendingAttachments?: PendingAttachment[];
  userProfiles?: Record<string, UserInfo>;
  notebookTemplate?: NotebookTemplateDto;
}
