import { create } from 'zustand';
import { MessageDto, ConversationTurn, ConversationMessage, ToolCall, ToolResult, AttachedFile, TokenUsage, isFileReference } from '../types/conversation';
import { api } from '../services/api';

// --- Types --------------------------------------------------------------

export type ConversationStatus =
  | 'idle'
  | 'loading'
  | 'sending'
  | 'streaming'
  | 'complete'
  | 'error';

export interface ConversationEntry {
  /** Full message history for this conversation */
  messages: MessageDto[];
  /** Turn-based grouped messages */
  turns: ConversationTurn[];
  /** Current turn being processed */
  currentTurn: ConversationTurn | null;
  /** Whether to use turn-based UI */
  turnBasedMode: boolean;
  /** Lifecycle status for UI rendering */
  status: ConversationStatus;
  /** Optimistic placeholder for the user message being sent */
  pendingUser?: MessageDto;
  /** Placeholder assistant message that is incrementally updated during `streaming` */
  pendingAssistant?: MessageDto;
  /** Latest error message (if any) */
  error?: string;
  /** Active SSE abort controller – allows cancelling a stream when navigating away */
  abortController?: AbortController;
}

// A helper for safely creating an empty entry when one does not yet exist
function createEmptyEntry(): ConversationEntry {
  return {
    messages: [],
    turns: [],
    currentTurn: null,
    turnBasedMode: false,
    status: 'idle',
  };
}

// --- Turn-Based Helper Functions ---

function createTurnFromMessages(messages: ConversationMessage[], attachments: AttachedFile[] = []): ConversationTurn {
  const assistantMessages = messages.filter(m => m.role === 'assistant');
  const assistantName = assistantMessages.length > 0 ? assistantMessages[0].assistantName || 'assistant' : 'assistant';
  
  return {
    turnIndex: messages[0]?.turnIndex || 0,
    assistantName,
    messages,
    toolCalls: [],
    toolResults: [],
    attachedFiles: attachments,
    usage: { promptTokens: 0, completionTokens: 0, totalTokens: 0 },
    timestamp: new Date(),
    isStreaming: false,
  };
}

function groupMessagesByTurns(messages: MessageDto[]): ConversationTurn[] {
  const turns: ConversationTurn[] = [];
  const turnMap = new Map<number, ConversationMessage[]>();
  const turnAttachments = new Map<number, AttachedFile[]>();
  
  // Convert MessageDto to ConversationMessage and group by turn
  messages.forEach((msg, index) => {
    // LEGACY: Handle old file reference messages (deprecated)
    if (isFileReference(msg) && msg.attachedNotebookFileId) {
      const currentTurnIndex = turnMap.size === 0 ? 0 : Math.max(...turnMap.keys());
      const attached: AttachedFile = {
        id: msg.id,
        notebookFileId: msg.attachedNotebookFileId,
        fileName: msg.content || 'file',
        fileType: 'other',
        fileSize: 0,
        uploadedAt: new Date(msg.created),
      };
      
      if (!turnAttachments.has(currentTurnIndex)) {
        turnAttachments.set(currentTurnIndex, []);
      }
      turnAttachments.get(currentTurnIndex)!.push(attached);
      return; // Skip adding separate message
    }
    
    // For now, we'll create a simple turn grouping based on conversation flow
    // User messages start new turns, assistant messages continue the current turn
    let turnIndex = 0;
    
    if (msg.role === 'user') {
      // Count previous user messages to determine turn index
      const previousUserMessages = messages.slice(0, index).filter(m => m.role === 'user');
      turnIndex = previousUserMessages.length;
    } else if (msg.role === 'assistant') {
      // Associate with the last user message's turn
      const lastUserIndex = messages.slice(0, index).findLastIndex(m => m.role === 'user');
      turnIndex = lastUserIndex >= 0 ? messages.slice(0, lastUserIndex + 1).filter(m => m.role === 'user').length - 1 : 0;
    }
    
    const conversationMessage: ConversationMessage = {
      id: msg.id,
      turnIndex,
      messageSequence: turnMap.get(turnIndex)?.length || 0,
      role: msg.role,
      content: msg.content,
      messageType: msg.role === 'user' ? 'user_input' : 'final_response', // Default classification
      assistantName: msg.assistantName,
      timestamp: new Date(msg.created),
      isEdited: msg.isEdited,
      lastEditedAt: msg.lastEditedAt ? new Date(msg.lastEditedAt) : undefined,
      originalContent: msg.originalContent,
    };
    
    if (!turnMap.has(turnIndex)) {
      turnMap.set(turnIndex, []);
    }
    turnMap.get(turnIndex)!.push(conversationMessage);
    
    // NEW: Handle multiple attachments per message
    if (msg.attachments && msg.attachments.length > 0) {
      const attachedFiles: AttachedFile[] = msg.attachments.map(att => ({
        id: `${msg.id}-${att.notebookFileId}`,
        notebookFileId: att.notebookFileId,
        fileName: att.fileName,
        fileType: att.fileType,
        fileSize: att.fileSize,
        uploadedAt: new Date(msg.created),
        status: 'complete' as const,
      }));
      
      if (!turnAttachments.has(turnIndex)) {
        turnAttachments.set(turnIndex, []);
      }
      turnAttachments.get(turnIndex)!.push(...attachedFiles);
    }
    
    // LEGACY: Handle single attachment during migration
    if (msg.attachedNotebookFileId && !isFileReference(msg)) {
      const legacyAttachment: AttachedFile = {
        id: `${msg.id}-legacy`,
        notebookFileId: msg.attachedNotebookFileId,
        fileName: 'file', // Default name for legacy attachments
        fileType: 'other',
        fileSize: 0,
        uploadedAt: new Date(msg.created),
        status: 'complete' as const,
      };
      
      if (!turnAttachments.has(turnIndex)) {
        turnAttachments.set(turnIndex, []);
      }
      turnAttachments.get(turnIndex)!.push(legacyAttachment);
    }
  });
  
  // Create turns from grouped messages
  turnMap.forEach((messages, turnIndex) => {
    const attachedFiles = turnAttachments.get(turnIndex) || [];
    const turn = createTurnFromMessages(messages, attachedFiles);
    turns.push(turn);
  });
  
  return turns.sort((a, b) => a.turnIndex - b.turnIndex);
}

// -----------------------------------------------------------------------
// Zustand store
// -----------------------------------------------------------------------

interface ConversationStore {
  /** Map of conversationId -> entry */
  entries: Record<string, ConversationEntry>;

  /** Selector that callers can use inside components to retrieve a single entry */
  select: (id: string) => ConversationEntry;

  /** Turn-based selectors */
  selectors: {
    getTurns: (conversationId: string) => ConversationTurn[];
    getCurrentTurn: (conversationId: string) => ConversationTurn | null;
    isTurnBasedMode: (conversationId: string) => boolean;
  };

  /** Async actions that operate on the backend & mutate local cache */
  actions: {
    /**
     * Fetches all messages for the provided conversation from the server and
     * populates the corresponding store entry.
     */
    fetch: (
      projectId: string,
      notebookId: string,
      conversationId: string,
    ) => Promise<void>;

    /** Invalidates the local cache so that the next `fetch` performs a network call */
    invalidate: (conversationId: string) => void;

    /** Cancels an in-flight SSE stream for the conversation (if any) */
    cancelStream: (conversationId: string) => void;

    /** Turn-based actions */
    enableTurnBasedMode: (conversationId: string) => void;
    disableTurnBasedMode: (conversationId: string) => void;
    groupMessagesByTurns: (conversationId: string) => void;
    addMessageToTurn: (conversationId: string, message: ConversationMessage) => void;
    addToolCallToTurn: (conversationId: string, toolCall: ToolCall) => void;
    addToolResultToTurn: (conversationId: string, result: ToolResult) => void;
    addFileToTurn: (conversationId: string, file: AttachedFile) => void;
    updateTurnUsage: (conversationId: string, turnIndex: number, usage: TokenUsage) => void;

    // 🚧 The following will be fleshed out in Day-2 of the refactor. For now, they
    // are declared so that components can compile.
    send: (
      projectId: string,
      notebookId: string,
      conversationId: string,
      text: string,
      assistantName?: string,
    ) => Promise<void>;

    edit: (
      projectId: string,
      notebookId: string,
      conversationId: string,
      messageId: string,
      newText: string,
    ) => Promise<void>;

    undo: (
      projectId: string,
      notebookId: string,
      conversationId: string,
    ) => Promise<void>;
  };
}

// We purposefully create the store as a *named* export so tests can import it
// without relying on React context.
// We intentionally type `set` and `get` as `any` to avoid extensive generic boilerplate.
// Their correct typings are supplied by Zustand at runtime and in IDE intellisense,
// but explicit `any` avoids the noImplicitAny compiler error.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export const useConversationStore = create<ConversationStore>()((set: any, get: any) => ({
  entries: {},

  select: (id: string): ConversationEntry => {
    return get().entries[id] ?? createEmptyEntry();
  },

  selectors: {
    getTurns: (conversationId: string): ConversationTurn[] => {
      const entry = get().entries[conversationId];
      return entry?.turns ?? [];
    },

    getCurrentTurn: (conversationId: string): ConversationTurn | null => {
      const entry = get().entries[conversationId];
      return entry?.currentTurn ?? null;
    },

    isTurnBasedMode: (conversationId: string): boolean => {
      const entry = get().entries[conversationId];
      return entry?.turnBasedMode ?? false;
    },
  },

  actions: {
    // -------------------------------------------------------------------
    async fetch(projectId: string, notebookId: string, conversationId: string) {
      // Guard: if we already have messages cached & not explicitly invalidated
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: { ...entry, status: 'loading' },
          },
        };
      });

      try {
        const convo = await api.projects.notebooks.conversations.get(
          projectId,
          notebookId,
          conversationId,
        );

        const messages = convo?.messages ?? [];

        set((state: ConversationStore): ConversationStore => {
          const entry = { ...state.entries[conversationId] };
          const turns = groupMessagesByTurns(messages);
          
          return {
            ...state,
            entries: {
              ...state.entries,
              [conversationId]: {
                ...entry,
                messages,
                turns,
                status: 'idle',
                error: undefined,
              },
            },
          };
        });
      } catch (err: any) {
        console.error('Conversation fetch failed', err);
        set((state: ConversationStore): ConversationStore => ({
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...state.entries[conversationId],
              status: 'error',
              error: err?.message ?? 'Failed to load conversation',
            },
          },
        }));
      }
    },

    // -------------------------------------------------------------------
    invalidate(conversationId: string) {
      set((state: ConversationStore): ConversationStore => {
        const { [conversationId]: _removed, ...rest } = state.entries;
        return { ...state, entries: rest };
      });
    },

    // -------------------------------------------------------------------
    cancelStream(conversationId: string) {
      const entry = get().entries[conversationId];
      entry?.abortController?.abort();
      set((state: ConversationStore): ConversationStore => ({
        ...state,
        entries: {
          ...state.entries,
          [conversationId]: {
            ...state.entries[conversationId],
            abortController: undefined,
            status: entry?.status === 'streaming' ? 'idle' : entry?.status ?? 'idle',
          },
        },
      }));
    },

    // -------------------------------------------------------------------
    // Turn-based actions
    enableTurnBasedMode(conversationId: string) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = groupMessagesByTurns(entry.messages);
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turnBasedMode: true,
              turns,
            },
          },
        };
      });
    },

    disableTurnBasedMode(conversationId: string) {
      set((state: ConversationStore): ConversationStore => ({
        ...state,
        entries: {
          ...state.entries,
          [conversationId]: {
            ...state.entries[conversationId],
            turnBasedMode: false,
          },
        },
      }));
    },

    groupMessagesByTurns(conversationId: string) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = groupMessagesByTurns(entry.messages);
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turns,
            },
          },
        };
      });
    },

    addMessageToTurn(conversationId: string, message: ConversationMessage) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = [...entry.turns];
        
        // Find or create the turn
        let targetTurn = turns.find(t => t.turnIndex === message.turnIndex);
        if (!targetTurn) {
          targetTurn = createTurnFromMessages([message]);
          turns.push(targetTurn);
        } else {
          targetTurn.messages.push(message);
          targetTurn.messages.sort((a, b) => a.messageSequence - b.messageSequence);
        }
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turns: turns.sort((a, b) => a.turnIndex - b.turnIndex),
              currentTurn: targetTurn,
            },
          },
        };
      });
    },

    addToolCallToTurn(conversationId: string, toolCall: ToolCall) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = [...entry.turns];
        
        if (entry.currentTurn) {
          const turnIndex = turns.findIndex(t => t.turnIndex === entry.currentTurn!.turnIndex);
          if (turnIndex >= 0) {
            turns[turnIndex] = {
              ...turns[turnIndex],
              toolCalls: [...turns[turnIndex].toolCalls, toolCall],
            };
          }
        }
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turns,
            },
          },
        };
      });
    },

    addToolResultToTurn(conversationId: string, result: ToolResult) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = [...entry.turns];
        
        if (entry.currentTurn) {
          const turnIndex = turns.findIndex(t => t.turnIndex === entry.currentTurn!.turnIndex);
          if (turnIndex >= 0) {
            turns[turnIndex] = {
              ...turns[turnIndex],
              toolResults: [...turns[turnIndex].toolResults, result],
            };
          }
        }
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turns,
            },
          },
        };
      });
    },

    addFileToTurn(conversationId: string, file: AttachedFile) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = [...entry.turns];
        
        if (entry.currentTurn) {
          const turnIndex = turns.findIndex(t => t.turnIndex === entry.currentTurn!.turnIndex);
          if (turnIndex >= 0) {
            turns[turnIndex] = {
              ...turns[turnIndex],
              attachedFiles: [...turns[turnIndex].attachedFiles, file],
            };
          }
        }
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turns,
            },
          },
        };
      });
    },

    updateTurnUsage(conversationId: string, turnIndex: number, usage: TokenUsage) {
      set((state: ConversationStore): ConversationStore => {
        const entry = state.entries[conversationId] ?? createEmptyEntry();
        const turns = [...entry.turns];
        
        const targetTurnIndex = turns.findIndex(t => t.turnIndex === turnIndex);
        if (targetTurnIndex >= 0) {
          turns[targetTurnIndex] = {
            ...turns[targetTurnIndex],
            usage,
          };
        }
        
        return {
          ...state,
          entries: {
            ...state.entries,
            [conversationId]: {
              ...entry,
              turns,
            },
          },
        };
      });
    },

    // -------------------------------------------------------------------
    // The following methods will be implemented later in the refactor.
    // For now, they simply throw to surface any accidental usage.
    async send() {
      throw new Error('conversationStore.actions.send not implemented yet');
    },
    async edit() {
      throw new Error('conversationStore.actions.edit not implemented yet');
    },
    async undo() {
      throw new Error('conversationStore.actions.undo not implemented yet');
    },
  },
})); 