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
  isUndoing: boolean;
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
  /**
   * Optional composer terminal policy (§6.7). When omitted the default restore-and-unlock
   * policy applies (no client-tool runner registered). P4 (main-chat client tools) registers
   * a policy that blocks the composer on pending_client_tool, executes the calls, and resumes —
   * swapping only this object, never the terminal owner or the SSE/transport layers.
   */
  composerTerminalPolicy?: ComposerTerminalPolicy;
  children: React.ReactNode;
}

/**
 * The composer's resolution of a turn-terminal outcome. The single terminal owner
 * (sendMessage's onComplete in useConversationActions) computes one of these from the
 * transport's terminal callback and hands it to the active ComposerTerminalPolicy.
 *
 * Only the `pending_client_tool` branch is policy-swappable (P4); success / cancelled /
 * error are fixed by the turnId persistence oracle (§6.4 / §11.3). The transport and the
 * SSE handler never change between policies — only the policy object does.
 */
export type ComposerTerminalOutcome =
  | { kind: 'success' }
  | { kind: 'cancelled'; turnId: string | null }
  | { kind: 'error'; turnId: string | null; message?: string }
  | { kind: 'pending_client_tool'; turnId: string | null };

/**
 * Seam for the composer's per-outcome behavior. The default policy (no client-tool runner
 * registered, i.e. today) clears on success and restores-and-unlocks on
 * pending_client_tool. A registered client-tool policy (P4) instead blocks the composer on
 * pending_client_tool, keeps the snapshot populated, executes the calls, and resumes.
 */
export interface ComposerTerminalPolicy {
  apply(outcome: ComposerTerminalOutcome): void;
}

export interface SendStreamState {
  snapshot: {
    draft: string;
    pendingAttachments: PendingAttachment[];
  } | null;
  turnId: string | null;
}

export interface ActionType {
  type: 'SET_MESSAGES' | 'ADD_MESSAGE' | 'UPDATE_MESSAGE' | 'REMOVE_LAST_TURN' | 'SET_STREAMING' | 'SET_ASSISTANT' | 'SET_DRAFT' | 'SET_ATTACHMENTS' | 'SET_EDITING' | 'SET_EDIT_ERROR' | 'SET_EDIT_LOADING' | 'APPEND_TOKEN' | 'FINALIZE_STREAMING_MESSAGE' | 'SET_ASSISTANTS' | 'SET_CONVERSATION_STARTERS' | 'SET_INITIALIZED' | 'SET_JUST_COMPLETED_STREAMING' | 'SET_CANCELLING' | 'SET_USER_PROFILES' | 'SET_STREAMING_ERROR' | 'SET_NOTEBOOK_TEMPLATE' |
  'START_STREAMING_TURN' | 'SET_TOOL_CALLS' | 'ENSURE_TOOL_CALL' | 'ADD_TOOL_RESULT' | 'ADD_FINAL_RESPONSE' | 'COMPLETE_STREAMING_TURN' | 'UPDATE_STREAMING_PROGRESS' | 'ADD_TOOL_ERROR' | 'SET_ACTIVE_TOOL_ACTIVITY' | 'ADD_ATTACHMENT' | 'REMOVE_ATTACHMENT' | 'CLEAR_ATTACHMENTS' | 'CONVERT_STREAMING_IDS' | 'CLEAR_STREAMING_CELL' | 'SET_PENDING_CELL_CLEAR' |
  'SET_STREAMING_MODE' | 'SET_UNDOING';
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
  _isUndoing?: boolean;
  pendingAttachments?: PendingAttachment[];
  userProfiles?: Record<string, UserInfo>;
  notebookTemplate?: NotebookTemplateDto;
}
