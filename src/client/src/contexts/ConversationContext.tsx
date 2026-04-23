import React, { createContext, useCallback, useContext, useEffect, useReducer, useMemo } from 'react';
import { api } from '../services/api';
import { userService } from '../services/userService';
import { useNotebook } from './NotebookContext';
import { useToast } from '../components/common/Toast';
import { ensureValidTokensForTemplate } from '../utils/notebookAuth';

import type { ConversationContextProps, ProviderProps } from './conversation/types';
export type { StreamingMode } from './conversation/types';
import { reducer, initialState } from './conversation/reducer';
import { getNotebookRuntimeReadyCache, checkRuntimeStatus } from './conversation/runtimeChecks';
import { useStreamingEventHandler } from './conversation/useStreamingEventHandler';
import { useConversationActions } from './conversation/useConversationActions';

const ConversationContext = createContext<ConversationContextProps | undefined>(undefined);

type ProviderAssistant = { name: string; model?: string; avatarUrl: string; id?: string };

function assistantsEqual(
  left: ProviderAssistant[] = [],
  right: ProviderAssistant[] = []
): boolean {
  if (left === right) return true;
  if (left.length !== right.length) return false;
  for (let i = 0; i < left.length; i += 1) {
    const a = left[i];
    const b = right[i];
    if (!a || !b) return false;
    if (
      a.name !== b.name ||
      a.model !== b.model ||
      a.avatarUrl !== b.avatarUrl ||
      a.id !== b.id
    ) {
      return false;
    }
  }
  return true;
}

export function ConversationProvider({ projectId, notebookId, conversationId, guideId, assistants: notebookAssistants, notebookTemplate: initialTemplate, onPreviewFile, onPreviewFileByPath, children }: ProviderProps) {
  const [state, dispatch] = useReducer(reducer, {
    ...initialState,
    assistants: notebookAssistants || [],
    notebookTemplate: initialTemplate,
    conversationStarters: initialTemplate?.conversationStarters || [],
    editError: undefined,
    isEditLoading: false,
    _isInitialized: false,
    _isCancelling: false,
    pendingAttachments: [],
    userProfiles: {},
  });

  const [currentStreamController, setCurrentStreamController] = React.useState<AbortController | null>(null);
  const templateStartersRef = React.useRef<string[]>([]);
  const inflightRuntimeChecksRef = React.useRef<Set<string>>(new Set());
  const runtimeReadyCacheRef = React.useRef<Set<string>>(getNotebookRuntimeReadyCache(notebookId));

  const { loadNotebookFiles, conversations, loadConversations } = useNotebook();
  const { showToast } = useToast();

  const isStreamingRef = React.useRef(state.isStreaming);
  const selectedAssistantRef = React.useRef(state.selectedAssistant);
  React.useEffect(() => {
    isStreamingRef.current = state.isStreaming;
    selectedAssistantRef.current = state.selectedAssistant;
  }, [state.isStreaming, state.selectedAssistant]);

  // Keep reducer assistants in sync with notebook-level assistants.
  // Defensive behavior: ignore transient empty updates once we already have assistants.
  useEffect(() => {
    const incoming = (notebookAssistants || []) as ProviderAssistant[];
    const current = (state.assistants || []) as ProviderAssistant[];

    if (incoming.length === 0 && current.length > 0) {
      return;
    }

    if (!assistantsEqual(current, incoming)) {
      dispatch({ type: 'SET_ASSISTANTS', payload: incoming });
    }
  }, [notebookAssistants, state.assistants]);

  // --- Refresh ---
  const refresh = useCallback(async () => {
    if (isStreamingRef.current) {
      return;
    }

    dispatch({ type: 'SET_STREAMING_ERROR', payload: undefined });

    if (!conversationId) {
      dispatch({ type: 'SET_MESSAGES', payload: [] });
      dispatch({ type: 'SET_ASSISTANT', payload: null });
      return;
    }
    try {
      const convo = await api.projects.notebooks.conversations.get(projectId, notebookId, conversationId);
      if (convo) {
        if (convo.messages) {
          const idsSet = new Set<string>();
          convo.messages.forEach(m => { if (m.userId) idsSet.add(m.userId); });
          try {
            const me = await userService.getCurrentUser();
            if (me?.id) idsSet.add(me.id);
          } catch {}

          const ids = Array.from(idsSet);
          const profiles = await Promise.all(ids.map(id => userService.getUserById(id as string).catch(() => null)));
          const profileMap: Record<string, any> = {};
          ids.forEach((id, idx) => { if (id && profiles[idx]) profileMap[id] = profiles[idx]; });
          dispatch({ type: 'SET_USER_PROFILES', payload: profileMap });
        }

        dispatch({ type: 'SET_MESSAGES', payload: convo.messages ?? [] });

        if (convo.assistantName) {
          dispatch({ type: 'SET_ASSISTANT', payload: convo.assistantName });

          const convoAssistant = state.assistants?.find(a => a.name === convo.assistantName);
          console.log('[llama-runtime] refresh: convoAssistant=', convo.assistantName, 'id=', convoAssistant?.id, 'full=', JSON.stringify(convoAssistant));
          if (convoAssistant?.id) {
            checkRuntimeStatus(projectId, notebookId, convoAssistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current).catch(() => {});
          } else {
            console.warn('[llama-runtime] refresh: SKIPPED runtime check — assistant.id is falsy');
          }
        } else if (convo.messages.length === 0 && !selectedAssistantRef.current) {
          dispatch({ type: 'SET_ASSISTANT', payload: null });
        }
      } else {
        console.warn(`Conversation ${conversationId} not found`);
        dispatch({ type: 'SET_MESSAGES', payload: [] });
        dispatch({ type: 'SET_ASSISTANT', payload: null });
      }
    } catch (error) {
      console.error('Failed to load conversation', error);
      dispatch({ type: 'SET_MESSAGES', payload: [] });
    }
  }, [projectId, notebookId, conversationId, state.assistants]);

  // --- Initialization effect ---
  useEffect(() => {
    if (!guideId) {
      console.error('No guideId provided - conversation cannot be initialized');
      dispatch({ type: 'SET_INITIALIZED', payload: true });
      return;
    }

    if (!notebookAssistants || notebookAssistants.length === 0) return;

    const template = initialTemplate;

    let defaultAssistantName = notebookAssistants.find(a => a.name.endsWith(' Guide'))?.name || template?.defaultAssistant;
    if (!defaultAssistantName && notebookAssistants.length > 0) {
      defaultAssistantName = notebookAssistants[0].name;
    }

    if (defaultAssistantName) {
      dispatch({ type: 'SET_ASSISTANT', payload: defaultAssistantName });

      const defaultAssistant = notebookAssistants.find(a => a.name === defaultAssistantName);
      if (defaultAssistant?.id) {
        checkRuntimeStatus(projectId, notebookId, defaultAssistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current).catch(() => {});
      }
    }

    if (template?.conversationStarters) {
      templateStartersRef.current = template.conversationStarters;
      dispatch({ type: 'SET_CONVERSATION_STARTERS', payload: template.conversationStarters });
    } else {
      templateStartersRef.current = [];
      dispatch({ type: 'SET_CONVERSATION_STARTERS', payload: [] });
    }

    dispatch({ type: 'SET_INITIALIZED', payload: true });
  }, [guideId, notebookAssistants]);

  // --- Rebind runtime cache on notebook change ---
  useEffect(() => {
    runtimeReadyCacheRef.current = getNotebookRuntimeReadyCache(notebookId);
  }, [notebookId]);

  // --- Invalidate runtime cache on tab focus ---
  useEffect(() => {
    const handleVisibility = () => {
      if (document.visibilityState === 'visible') {
        runtimeReadyCacheRef.current.clear();
      }
    };
    document.addEventListener('visibilitychange', handleVisibility);
    return () => document.removeEventListener('visibilitychange', handleVisibility);
  }, []);

  // --- Refresh on conversation/notebook change ---
  useEffect(() => {
    const timeoutId = setTimeout(() => {
      if (state._isInitialized && !state._justCompletedStreaming) {
        refresh();
      }
    }, 10);

    return () => clearTimeout(timeoutId);
  }, [conversationId, notebookId, state._isInitialized, state._justCompletedStreaming]);

  // --- Load assistant-specific starters ---
  useEffect(() => {
    const loadAssistantStarters = async () => {
      if (!state._isInitialized) return;
      if (state.messages.length > 0) return;
      if (!state.selectedAssistant) return;
      try {
        const starters = await api.projects.assistants.getConversationStarters(state.selectedAssistant, projectId);
        if (Array.isArray(starters) && starters.length > 0) {
          dispatch({ type: 'SET_CONVERSATION_STARTERS', payload: starters });
        } else {
          dispatch({ type: 'SET_CONVERSATION_STARTERS', payload: templateStartersRef.current });
        }
      } catch (err) {
        dispatch({ type: 'SET_CONVERSATION_STARTERS', payload: templateStartersRef.current });
      }
    };
    loadAssistantStarters();
  }, [state.selectedAssistant, state.messages.length, state._isInitialized, projectId]);

  // --- Clear "just completed streaming" flag ---
  useEffect(() => {
    if (state._justCompletedStreaming) {
      const timeout = setTimeout(() => {
        dispatch({ type: 'SET_JUST_COMPLETED_STREAMING', payload: false });
      }, 100);
      return () => clearTimeout(timeout);
    }
  }, [state._justCompletedStreaming]);

  // --- Silent OAuth token refresh timer ---
  useEffect(() => {
    const template = state.notebookTemplate;
    if (!template) return;

    let cancelled = false;

    const run = async () => {
      try {
        const result = await ensureValidTokensForTemplate(template, projectId);
        if (!cancelled && result.needsAuth) {
          const missing = result.missingProviders.map(p => p.id).join(', ');
          showToast({
            type: 'warning',
            title: 'Authentication Needed',
            message: `Please reconnect these services: ${missing}`
          });
        }
      } catch {
        // Best-effort
      }
    };

    run();
    const id = setInterval(run, 4 * 60 * 1000);
    return () => { cancelled = true; clearInterval(id); };
  }, [state.notebookTemplate, projectId, showToast]);

  // --- Cleanup on conversation change ---
  useEffect(() => {
    if (currentStreamController) {
      console.log('🔥 Conversation changed, cancelling active stream');
      currentStreamController.abort();
      setCurrentStreamController(null);

      dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
      dispatch({ type: 'COMPLETE_STREAMING_TURN' });
      dispatch({ type: 'SET_CANCELLING', payload: false });
      dispatch({ type: 'CLEAR_ATTACHMENTS' });
    }

    dispatch({ type: 'SET_MESSAGES', payload: [] });
  }, [conversationId]);

  // --- Cleanup on unmount ---
  useEffect(() => {
    return () => {
      if (currentStreamController) {
        currentStreamController.abort();
        setCurrentStreamController(null);
      }
    };
  }, []);

  // --- Custom hooks for streaming events and actions ---
  const handleStreamingEvent = useStreamingEventHandler(dispatch, state, {
    loadNotebookFiles, loadConversations, conversations, showToast,
    projectId, notebookId, conversationId, setCurrentStreamController,
  });

  const assistants = useMemo(() => state.assistants ?? [], [state.assistants]);
  const conversationStarters = useMemo(() => state.conversationStarters ?? [], [state.conversationStarters]);
  const pendingAttachments = useMemo(() => state.pendingAttachments ?? [], [state.pendingAttachments]);

  const assistantByName = useMemo(() => {
    const map: Record<string, { name: string; model?: string; avatarUrl: string; id?: string }> = {};
    (assistants || []).forEach((a: any) => {
      if (a?.name) {
        map[a.name] = { name: a.name, model: a.model, avatarUrl: a.avatarUrl, id: a.id };
      }
    });
    return map;
  }, [assistants]);

  const actions = useConversationActions(dispatch, state, {
    projectId, notebookId, conversationId,
    handleStreamingEvent, showToast, loadNotebookFiles,
    currentStreamController, setCurrentStreamController,
    inflightRuntimeChecksRef, runtimeReadyCacheRef, assistantByName,
  });

  const currentAssistant = useMemo(() => {
    const name = state.selectedAssistant || undefined;
    return name ? assistantByName[name] : undefined;
  }, [state.selectedAssistant, assistantByName]);

  const value: ConversationContextProps = {
    ...state,
    sendMessage: actions.sendMessage,
    editAssistantMessage: actions.editAssistantMessage,
    undoLastTurn: actions.undoLastTurn,
    setSelectedAssistant: actions.setSelectedAssistant,
    setDraftUserContent: actions.setDraftUserContent,
    startEditingAssistant: actions.startEditingAssistant,
    cancelEditingAssistant: actions.cancelEditingAssistant,
    refresh,
    assistants,
    currentAssistant,
    assistantByName,
    conversationStarters,
    editError: state.editError,
    isEditLoading: state.isEditLoading ?? false,
    isInitialized: state._isInitialized ?? false,
    isCancelling: state._isCancelling ?? false,
    streamingError: state.streamingError,
    streamingMode: state.streamingMode ?? 'at-rest',
    activeStreamingUser: state.activeStreamingUser,
    pendingAttachments,
    userProfiles: state.userProfiles ?? {},
    handleStreamingEvent,
    setStreamingMode: actions.setStreamingMode,
    cancelStream: actions.cancelStream,
    addPendingAttachment: actions.addPendingAttachment,
    removePendingAttachment: actions.removePendingAttachment,
    onPreviewFile,
    onPreviewFileByPath,
  };

  return <ConversationContext.Provider value={value}>{children}</ConversationContext.Provider>;
}

export function useConversation() {
  const ctx = useContext(ConversationContext);
  if (!ctx) {
    throw new Error('useConversation must be used within ConversationProvider');
  }
  return ctx;
}

export { reducer } from './conversation/reducer';
