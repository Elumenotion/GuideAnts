import { useCallback, useRef } from 'react';
import type { AttachedFileDto, MessageDto, PendingAttachment } from '../../types/conversation';
import { api } from '../../services/api';
import { fileTypeFromUploadType, normalizeRelativePath, toPendingUploadType, uploadTypeToServer } from '../../utils/attachments';
import { userService } from '../../services/userService';
import { ensureValidTokensForTemplate } from '../../utils/notebookAuth';
import { checkRuntimeStatus, getRuntimeBlockingMessage, dispatchRuntimeStatusWindowEvent } from './runtimeChecks';
import type { ActionType, ComposerTerminalOutcome, ComposerTerminalPolicy, ExtendedConversationState, SendStreamState, StreamingMode } from './types';

interface ActionDeps {
  projectId: string;
  notebookId: string;
  conversationId: string;
  handleStreamingEvent: (
    event: { type: string; data: any },
    source?: 'send' | 'observer',
  ) => void;
  showToast: (opts: any) => void;
  loadNotebookFiles: () => Promise<void>;
  currentStreamController: AbortController | null;
  setCurrentStreamController: (c: AbortController | null) => void;
  observerStreamController: AbortController | null;
  setObserverStreamController: (c: AbortController | null) => void;
  sendStreamRef: React.MutableRefObject<AbortController | null>;
  observerStreamRef: React.MutableRefObject<AbortController | null>;
  inflightRuntimeChecksRef: React.MutableRefObject<Set<string>>;
  runtimeReadyCacheRef: React.MutableRefObject<Set<string>>;
  assistantByName: Record<string, { name: string; model?: string; avatarUrl: string; id?: string }>;
  activeStreamTurnId: string | null;
  setActiveStreamTurnId: (turnId: string | null) => void;
  getActiveStreamTurnId: () => string | null;
  refreshConversation: (options?: { force?: boolean }) => Promise<void>;
  sendStreamStateRef?: React.MutableRefObject<SendStreamState | null>;
  pendingStopRef?: React.MutableRefObject<boolean>;
  /**
   * Composer terminal policy (§6.7). Optional — defaults to the restore-and-unlock policy
   * (no client-tool runner registered today). A P4 client-tool policy swaps in here and is
   * the ONLY thing that changes; the transport, the SSE handler, and the single terminal
   * owner below are identical for both.
   */
  composerTerminalPolicy?: ComposerTerminalPolicy;
}

function attachmentDtoToPendingAttachment(attachment: AttachedFileDto): PendingAttachment | null {
  const relativePath = attachment.relativePath
    ? normalizeRelativePath(attachment.relativePath)
    : undefined;

  if (!relativePath && !attachment.notebookFileId) {
    return null;
  }

  return {
    notebookFileId: relativePath
      ? `path:${relativePath}`
      : attachment.notebookFileId!,
    relativePath,
    fileName: attachment.fileName,
    uploadType: toPendingUploadType(attachment.uploadType, attachment.fileName),
  };
}

export function useConversationActions(
  dispatch: React.Dispatch<ActionType>,
  state: ExtendedConversationState,
  deps: ActionDeps,
) {
  const {
    projectId, notebookId, conversationId,
    handleStreamingEvent, showToast, loadNotebookFiles,
    setCurrentStreamController,
    setObserverStreamController,
    sendStreamRef, observerStreamRef,
    inflightRuntimeChecksRef, runtimeReadyCacheRef, assistantByName,
    activeStreamTurnId, setActiveStreamTurnId, getActiveStreamTurnId, refreshConversation,
    composerTerminalPolicy,
  } = deps;

  const localSendStreamStateRef = useRef<SendStreamState | null>(null);
  const sendStreamStateRef = deps.sendStreamStateRef ?? localSendStreamStateRef;
  const localPendingStopRef = useRef(false);
  const pendingStopRef = deps.pendingStopRef ?? localPendingStopRef;
  const stopPostedForTurnRef = useRef<string | null>(null);
  const stopRetryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const stopReconcileTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const stopReconcileInFlightRef = useRef(false);
  const stopFailureNotifiedRef = useRef(false);
  const stopTargetRef = useRef<{
    turnId: string;
    sendController: AbortController | null;
    observerController: AbortController | null;
  } | null>(null);

  const clearStopAttempt = useCallback(() => {
    if (stopRetryTimerRef.current !== null) {
      clearTimeout(stopRetryTimerRef.current);
      stopRetryTimerRef.current = null;
    }
    if (stopReconcileTimerRef.current !== null) {
      clearTimeout(stopReconcileTimerRef.current);
      stopReconcileTimerRef.current = null;
    }
    stopPostedForTurnRef.current = null;
    stopTargetRef.current = null;
    stopFailureNotifiedRef.current = false;
  }, []);

  const clearPendingStop = useCallback(() => {
    clearStopAttempt();
    pendingStopRef.current = false;
  }, [clearStopAttempt, pendingStopRef]);

  const adoptSendStream = useCallback((controller: AbortController | null) => {
    setCurrentStreamController(controller);
  }, [setCurrentStreamController]);

  const adoptObserverStream = useCallback((controller: AbortController | null) => {
    setObserverStreamController(controller);
  }, [setObserverStreamController]);

  const abortObserverStream = useCallback(() => {
    const existing = observerStreamRef.current;
    if (existing && !existing.signal.aborted) {
      existing.abort();
    }
    setObserverStreamController(null);
  }, [observerStreamRef, setObserverStreamController]);

  const applyComposerTerminalPolicy = useCallback((messagePersisted: boolean) => {
    const snapshot = sendStreamStateRef.current?.snapshot;
    if (!snapshot) {
      return;
    }

    // Draft rules (owner, 2026-08-31): the ONLY terminal that restores the draft is UNDO
    // (undoLastTurn). A confirmed Stop / reconciled failure that persisted the message has a
    // consumed input that lives in the transcript -- restoring it into the composer is the
    // "previous message reappears in the draft" defect. The draft is already '' from send
    // time; only the attachment chips need a decision (consumed vs. lost with the send).
    if (messagePersisted) {
      dispatch({ type: 'CLEAR_ATTACHMENTS' });
    } else {
      dispatch({ type: 'SET_DRAFT', payload: snapshot.draft });
      dispatch({ type: 'SET_ATTACHMENTS', payload: snapshot.pendingAttachments });
    }
  }, [dispatch, sendStreamStateRef]);

  const clearSendStreamState = useCallback(() => {
    sendStreamStateRef.current = null;
  }, [sendStreamStateRef]);

  /**
   * The single composer terminal owner (P2 / §6.3). Driven by the transport's
   * onComplete(terminalEventType), which fires exactly once per turn — so composer state is
   * finalized exactly once, by one owner, reading only refs + the snapshot (never the
   * render-captured state.* that froze the SSE handler's stale closure).
   *
   * Routed through the ComposerTerminalPolicy seam (§6.7): success / cancelled / error are
   * fixed by the turnId persistence oracle; only pending_client_tool is swappable (default
   * restore-and-unlock today; a P4 client-tool policy blocks instead).
   */
  const applyComposerTerminalOutcome = useCallback((outcome: ComposerTerminalOutcome) => {
    const snapshot = sendStreamStateRef.current?.snapshot ?? null;
    const turnId = sendStreamStateRef.current?.turnId ?? null;

    switch (outcome.kind) {
      case 'success':
        // The turn consumed the input. The draft was cleared at send time (P0); clear any
        // attachments that were re-added during the turn and release the snapshot.
        dispatch({ type: 'CLEAR_ATTACHMENTS' });
        clearSendStreamState();
        break;

      case 'cancelled':
      case 'error': {
        // turnId persistence oracle (§11.3) + draft rules: the server persists the user
        // message before emitting turn_created, so a known turnId means the input was
        // CONSUMED into the transcript → the draft must stay empty (the only draft restore
        // is undo). No turnId means the send was lost server-side → restore draft + chips
        // so the user's input is not silently dropped.
        const persisted = Boolean(outcome.turnId || turnId);
        if (snapshot) {
          if (persisted) {
            dispatch({ type: 'CLEAR_ATTACHMENTS' });
          } else {
            dispatch({ type: 'SET_DRAFT', payload: snapshot.draft });
            dispatch({ type: 'SET_ATTACHMENTS', payload: snapshot.pendingAttachments });
          }
        }
        clearSendStreamState();
        break;
      }

      case 'pending_client_tool':
        // Swappable seam. Default policy: the turn is paused server-side, not consumed —
        // restore the user's input and release the snapshot so the composer is at-rest and
        // the turn stays cancellable/undoable. A registered client-tool policy overrides this
        // (keep the snapshot, block the composer, execute + resume).
        if (composerTerminalPolicy) {
          composerTerminalPolicy.apply({ kind: 'pending_client_tool', turnId: outcome.turnId ?? turnId });
        } else if (snapshot) {
          // A pending_client_tool terminal carries a turnId: the input is persisted and the
          // turn is resumable, so this is a consumed end-of-turn -- draft stays empty
          // (Rule 1). Chips are cleared with the send; a client-tool policy that keeps the
          // composer busy overrides this via composerTerminalPolicy above.
          dispatch({ type: 'CLEAR_ATTACHMENTS' });
          clearSendStreamState();
        }
        break;
    }
  }, [clearSendStreamState, composerTerminalPolicy, dispatch, sendStreamStateRef]);

  const completeConfirmedCancellation = useCallback((target: {
    turnId: string;
    sendController: AbortController | null;
    observerController: AbortController | null;
  }) => {
    if (target.sendController && sendStreamRef.current === target.sendController) {
      target.sendController.abort();
      setCurrentStreamController(null);
    }
    if (target.observerController && observerStreamRef.current === target.observerController) {
      target.observerController.abort();
      setObserverStreamController(null);
    }
    clearPendingStop();
    setActiveStreamTurnId(null);
    applyComposerTerminalPolicy(true);
    dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    dispatch({ type: 'COMPLETE_STREAMING_TURN' });
    dispatch({ type: 'SET_STREAMING', payload: false });
    dispatch({ type: 'SET_CANCELLING', payload: false });
    clearSendStreamState();
    void refreshConversation({ force: true }).catch(error => {
      console.warn('Failed to refresh conversation after confirmed cancel:', error);
    });
    // A confirmed Stop is a turn-terminal outcome too: tools may have registered files before
    // the turn was stopped, and this path does NOT go through onComplete (its pendingStop
    // guard early-returns), so refresh the file tree here to keep the "every terminal
    // refreshes the tree once" invariant.
    loadNotebookFiles().catch(error => {
      console.warn('Failed to refresh notebook files after confirmed cancel:', error);
    });
  }, [
    applyComposerTerminalPolicy,
    clearPendingStop,
    clearSendStreamState,
    dispatch,
    loadNotebookFiles,
    observerStreamRef,
    refreshConversation,
    sendStreamRef,
    setActiveStreamTurnId,
    setCurrentStreamController,
    setObserverStreamController,
  ]);

  const requestServerStop = useCallback((turnId: string) => {
    if (stopPostedForTurnRef.current === turnId) {
      return;
    }

    stopPostedForTurnRef.current = turnId;
    const target = {
      turnId,
      sendController: sendStreamRef.current,
      observerController: observerStreamRef.current,
    };
    stopTargetRef.current = target;
    const scheduleStopRetry = () => {
      if (stopRetryTimerRef.current !== null) {
        return;
      }
      stopRetryTimerRef.current = setTimeout(() => {
        stopRetryTimerRef.current = null;
        postStop();
      }, 250);
    };
    const postStop = () => {
      void api.projects.notebooks.conversations
        .cancelTurn(projectId, notebookId, conversationId, turnId)
        .then(() => {
          // A newer turn may have started while the cancel request was in flight. Do not let
          // the old request complete or refresh the newer turn's workflow.
          const currentTurnId = getActiveStreamTurnId();
          if (stopPostedForTurnRef.current !== turnId
            || stopTargetRef.current !== target
            || (currentTurnId && currentTurnId !== turnId)) {
            // The target stream is no longer the active workflow (for example, another client
            // started a newer turn). Release only the stale Stop request; do not finalize the
            // newer workflow or claim that this request stopped it.
            if (stopTargetRef.current === target) {
              clearPendingStop();
              dispatch({ type: 'SET_CANCELLING', payload: false });
            }
            return;
          }

          // The server has now persisted the terminal state, released the lock, and
          // unregistered the worker. Finalize the local workflow only after that boundary.
          completeConfirmedCancellation(target);
        })
        .catch(err => {
          const currentTurnId = getActiveStreamTurnId();
          const targetIsCurrent = stopPostedForTurnRef.current === turnId
            && stopTargetRef.current === target
            && (!currentTurnId || currentTurnId === turnId);

          if (!targetIsCurrent) {
            if (stopTargetRef.current === target) {
              clearPendingStop();
              dispatch({ type: 'SET_CANCELLING', payload: false });
            }
            return;
          }

          if (err?.status === 409) {
            // The server accepted cancellation but has not reached the lifecycle boundary.
            // Keep the UI locked and retry without claiming that Stop completed.
            scheduleStopRetry();
            return;
          }

          console.warn('Failed to request server stream cancellation:', err);
          // The server did not confirm the lifecycle boundary. Keep the workflow blocked, but
          // continue retrying instead of claiming that the conversation is available.
          if (!stopFailureNotifiedRef.current) {
            stopFailureNotifiedRef.current = true;
            showToast({
              type: 'error',
              title: 'Stop Failed',
              message: 'The server did not confirm cancellation. Retrying while the response is still running.',
            });
          }
          scheduleStopRetry();
        });
    };

    postStop();
  }, [
    projectId,
    notebookId,
    conversationId,
    completeConfirmedCancellation,
    getActiveStreamTurnId,
    dispatch,
    observerStreamRef,
    sendStreamRef,
    showToast,
    clearPendingStop,
  ]);

  const reconcileLostSendAndStop = useCallback((content: string, error: Error) => {
    if (stopReconcileInFlightRef.current) {
      return;
    }

    stopReconcileInFlightRef.current = true;
    pendingStopRef.current = true;
    dispatch({ type: 'SET_CANCELLING', payload: true });

    void (async () => {
      try {
        const conversation = await api.projects.notebooks.conversations.get(
          projectId,
          notebookId,
          conversationId,
        );
        const serverTurnId = conversation?.activeTurn
          && ['streaming', 'pending_client_tool'].includes((conversation.activeTurn.status ?? '').toLowerCase())
          ? conversation.activeTurn.turnId
          : null;
        if (serverTurnId) {
          if (sendStreamStateRef.current) {
            sendStreamStateRef.current.turnId = serverTurnId;
          }
          requestServerStop(serverTurnId);
          return;
        }

        if (!conversation
          || conversation.lock
          || conversation.streamingPreview) {
          throw new Error('The server still reports an active conversation lifecycle without a turn id.');
        }

        const messagePersisted = (conversation?.messages ?? []).some(message =>
          message.role?.toLowerCase() === 'user' && message.content === content);
        if (messagePersisted) {
          await refreshConversation({ force: true });
        }
        applyComposerTerminalPolicy(messagePersisted);
        clearPendingStop();
        dispatch({ type: 'SET_STREAMING', payload: false });
        dispatch({ type: 'SET_CANCELLING', payload: false });
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'CONVERT_STREAMING_IDS' });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
        adoptSendStream(null);
        setActiveStreamTurnId(null);
        dispatch({ type: 'SET_STREAMING_ERROR', payload: error.message || 'Chat request failed' });
        showToast({
          type: 'error',
          title: 'Chat Request Failed',
          message: error.message || 'Chat request failed',
        });
        clearSendStreamState();
      } catch (reconcileError) {
        if (!stopFailureNotifiedRef.current) {
          stopFailureNotifiedRef.current = true;
          showToast({
            type: 'error',
            title: 'Stop Not Confirmed',
            message: 'The server did not confirm whether the response stopped. Retrying.',
          });
        }
        console.warn('Failed to reconcile conversation after stream failure:', reconcileError);
        if (stopReconcileTimerRef.current === null) {
          stopReconcileTimerRef.current = setTimeout(() => {
            stopReconcileTimerRef.current = null;
            reconcileLostSendAndStop(content, error);
          }, 250);
        }
      } finally {
        stopReconcileInFlightRef.current = false;
      }
    })();
  }, [
    adoptSendStream,
    applyComposerTerminalPolicy,
    clearPendingStop,
    clearSendStreamState,
    conversationId,
    dispatch,
    notebookId,
    pendingStopRef,
    projectId,
    refreshConversation,
    requestServerStop,
    sendStreamStateRef,
    setActiveStreamTurnId,
    showToast,
  ]);

  const onTurnIdAssigned = useCallback((turnId: string): boolean => {
    if (pendingStopRef.current) {
      const pendingTargetTurnId = stopTargetRef.current?.turnId;
      if (pendingTargetTurnId && pendingTargetTurnId !== turnId) {
        // A newer turn may be announced by the observer while an older Stop request is
        // completing. Never retarget Stop at the newer workflow.
        return false;
      }
      requestServerStop(turnId);
    }
    return true;
  }, [requestServerStop]);

  const sendMessage = useCallback(
    async (content: string, attachments: PendingAttachment[] = []) => {
      if (state._isUndoing || state.isStreaming || state._isCancelling) {
        return;
      }

      const isRuntimeNotReadyError = (error: any): boolean => {
        const bodyText = (() => {
          const body = error?.body;
          if (typeof body === 'string') return body;
          if (body && typeof body === 'object') {
            const pieces = [body.error, body.message, body.detail]
              .filter((v: unknown) => typeof v === 'string') as string[];
            return pieces.join(' ');
          }
          return '';
        })();
        const raw = `${error?.message ?? ''} ${bodyText}`.toLowerCase();
        return raw.includes('model is not loaded')
          || raw.includes('no model is loaded')
          || raw.includes('does not have a model loaded')
          || raw.includes('server has no model loaded');
      };

      const authStatus = await ensureValidTokensForTemplate(state.notebookTemplate || null, projectId);

      if (authStatus.needsAuth) {
        const missingProviderNames = authStatus.missingProviders.map(p => p.id).join(', ');
        showToast({
          type: 'error',
          title: 'Authentication Required',
          message: `Please reconnect these services: ${missingProviderNames}`
        });
        return;
      }

      const assistantName = state.selectedAssistant || (() => { throw new Error('No assistant selected when sending message'); })();
      const assistant = (state.assistants || []).find((candidate: any) => candidate?.name === assistantName);

      // Optimistic render: paint the user's message and the placeholder immediately,
      // before the runtime preflight. Every early-return below must roll this back.
      const attList = (attachments && attachments.length > 0) ? attachments : (state.pendingAttachments ?? []);

      const userMessage: MessageDto = {
        id: `tmp-${Date.now()}`,
        role: 'user',
        content,
        created: new Date().toISOString(),
        isEdited: false,
        attachments: attList.map(a => ({
          notebookFileId: a.relativePath
            ? `path:${normalizeRelativePath(a.relativePath)}`
            : a.notebookFileId,
          relativePath: a.relativePath ? normalizeRelativePath(a.relativePath) : undefined,
          uploadType: uploadTypeToServer(a.uploadType),
          fileName: a.fileName,
          fileType: fileTypeFromUploadType(a.uploadType),
          fileSize: 0,
          type: 'Referenced' as const
        }))
      } as MessageDto;

      const placeholderAssistant: MessageDto = {
        id: `streaming-${Date.now()}`,
        role: 'assistant',
        content: '',
        created: new Date().toISOString(),
        isEdited: false,
        assistantName: state.selectedAssistant || undefined,
        streaming: true,
      } as MessageDto;

      // Reset stream-cancellation state BEFORE the optimistic dispatches flip
      // isStreaming to true. The Stop button renders on isStreaming alone (no
      // currentTurn guard), so it's clickable for the entire runtime-preflight
      // window below; if that reset ran after the preflight (as it used to),
      // a Stop click during the preflight would queue pendingStopRef, and this
      // clearPendingStop() would then wipe it out before onTurnIdAssigned ever
      // saw it — silently discarding the stop. Also clear any stale turn id
      // from a previous send here so an early Stop on this new send can't
      // target the wrong turn (see stale-active-stream-turn-id.md).
      dispatch({ type: 'SET_CANCELLING', payload: false });
      dispatch({ type: 'SET_STREAMING_ERROR', payload: undefined });
      clearPendingStop();
      setActiveStreamTurnId?.(null);

      sendStreamStateRef.current = {
        snapshot: {
          draft: content,
          pendingAttachments: [...attList],
        },
        turnId: null,
      };

      dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'sending' } });
      dispatch({ type: 'SET_DRAFT', payload: '' });
      // P0 (turn-terminal ownership): clear the composer's attachment chips at SEND time,
      // not at terminal. The snapshot above preserves them for every restore-on-failure path.
      dispatch({ type: 'CLEAR_ATTACHMENTS' });
      dispatch({ type: 'ADD_MESSAGE', payload: userMessage });
      dispatch({ type: 'ADD_MESSAGE', payload: placeholderAssistant });

      const rollbackOptimisticSend = () => {
        // REMOVE_LAST_TURN pops trailing messages through the last user message —
        // exactly the placeholder + user message added above.
        dispatch({ type: 'REMOVE_LAST_TURN' });
        const snapshot = sendStreamStateRef.current?.snapshot;
        dispatch({ type: 'SET_DRAFT', payload: snapshot?.draft ?? content });
        dispatch({ type: 'SET_ATTACHMENTS', payload: snapshot?.pendingAttachments ?? attList });
        dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'at-rest' } });
        // A Stop click during the runtime preflight (window between the
        // optimistic dispatches above and this rollback) sets _isCancelling
        // true; undo that here so it can't leak into the next send attempt.
        dispatch({ type: 'SET_CANCELLING', payload: false });
        clearPendingStop();
        setActiveStreamTurnId?.(null);
        clearSendStreamState();
      };

      if (assistant?.id) {
        try {
          const runtimeStatus = await checkRuntimeStatus(projectId, notebookId, assistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current);
          if (!runtimeStatus) {
            rollbackOptimisticSend();
            return;
          }
          if (runtimeStatus.state !== 'ready') {
            if (runtimeStatus.state === 'failed' || runtimeStatus.state === 'invalid') {
              showToast({
                type: 'error',
                title: runtimeStatus.state === 'invalid' ? 'Incompatible Local Models' : 'Local Runtime Error',
                message: getRuntimeBlockingMessage(runtimeStatus)
              });
            }
            rollbackOptimisticSend();
            return;
          }
        } catch (error: any) {
          console.error('Failed to check local runtime status:', error);
          showToast({
            type: 'error',
            title: 'Runtime Error',
            message: `Failed to check model runtime status: ${error.message}`
          });
          rollbackOptimisticSend();
          return;
        }
      }

      // Send exclusively owns token appends for this turn.
      abortObserverStream();

      const controller = new AbortController();
      const streamTurn = { current: null as string | null };
      adoptSendStream(controller);

      dispatch({ type: 'START_STREAMING_TURN' });
      let idleStopRequested = false;

      // activeStreamTurnId is assigned from the SSE `turnId` event once the
      // stream opens (see useStreamingEventHandler); no need to pre-fetch
      // the conversation snapshot just to learn it a few hundred ms early.
      // A Stop click that races ahead of that event is queued via
      // pendingStopRef/onTurnIdAssigned and re-issued once the id lands
      // (pre-existing behavior, unchanged by this removal).

      try {
        await api.projects.notebooks.conversations.sendMessageStream(
          projectId,
          notebookId,
          conversationId,
          {
            instructions: content,
            assistantName: state.selectedAssistant || (() => { throw new Error('No assistant selected when sending message'); })(),
            attachments: attList.map(a => ({
              notebookFileId: a.relativePath ? null : a.notebookFileId,
              relativePath: a.relativePath ? normalizeRelativePath(a.relativePath) : null,
              uploadType: uploadTypeToServer(a.uploadType),
            })),
          } as any,
          (event) => {
            if (event.type === 'turn_created' && event.data?.turnId) {
              streamTurn.current = event.data.turnId;
              if (sendStreamStateRef.current) {
                sendStreamStateRef.current.turnId = event.data.turnId;
              }
            }
            handleStreamingEvent(event, 'send');
          },
          (error) => {
            if (sendStreamRef.current !== controller) {
              return;
            }
            const isUserCancel = error.name === 'AbortError';
            const isIdleTimeout = error.name === 'StreamIdleTimeoutError';

            if (isUserCancel) {
              console.log('SSE client disconnected; server run may continue');
              adoptSendStream(null);
              if (!pendingStopRef.current) {
                dispatch({ type: 'SET_CANCELLING', payload: false });
                clearSendStreamState();
              }
              return;
            }

            if (isIdleTimeout) {
              if (idleStopRequested || pendingStopRef.current) {
                console.log('Stream idle timeout is already stopping the server turn');
                return;
              }
              const persistedTurnId = streamTurn.current ?? sendStreamStateRef.current?.turnId;
              if (persistedTurnId) {
                // An idle timeout is a transport failure, not proof that the server worker
                // stopped. Use the same confirmed Stop boundary as an explicit button click.
                pendingStopRef.current = true;
                dispatch({ type: 'SET_CANCELLING', payload: true });
                requestServerStop(persistedTurnId);
                return;
              }
              reconcileLostSendAndStop(content, error);
              return;
            }

            const persistedTurnId = streamTurn.current ?? sendStreamStateRef.current?.turnId;
            if (persistedTurnId) {
              // A transport failure only disconnects this client. The server deliberately keeps
              // the worker alive after SSE disconnect, so do not unlock the composer until the
              // same hard-stop boundary used by the Stop button has completed.
              pendingStopRef.current = true;
              dispatch({ type: 'SET_CANCELLING', payload: true });
              requestServerStop(persistedTurnId);
              return;
            }

            console.error('Streaming error:', error);
            reconcileLostSendAndStop(content, error);
          },
          (terminalEventType?: string) => {
            // Single composer owner for transport-delivered terminal outcomes (P2): the
            // transport invokes this exactly once — with the terminal SSE event type
            // (complete / cancelled / error / pending_client_tool) or with no type on a
            // clean body close. Composer state decisions here read only refs and the
            // send snapshot — never render-captured state.* — so a stale closure in the
            // SSE event handler cannot freeze the composer.
            if (sendStreamRef.current !== controller) {
              return; // A newer turn superseded this one; its owner finalizes instead.
            }
            if (pendingStopRef.current) {
              // A terminal SSE marker does not prove the cancel endpoint completed its durable
              // lifecycle work. Let the confirmed Stop response finalize this workflow.
              return;
            }
            const persistedTurnId =
              sendStreamStateRef.current?.turnId ?? streamTurn.current ?? null;

            clearPendingStop();
            setActiveStreamTurnId(null);
            dispatch({ type: 'COMPLETE_STREAMING_TURN' });
            dispatch({ type: 'SET_CANCELLING', payload: false });
            adoptSendStream(null);

            // Route the composer through the policy seam (§6.7). The outcome's turnId carries
            // the persistence oracle; the policy (default, or a P4 client-tool policy) decides
            // the composer's behavior per outcome.
            const outcome: ComposerTerminalOutcome = terminalEventType === 'cancelled'
              ? { kind: 'cancelled', turnId: persistedTurnId }
              : terminalEventType === 'error'
                ? { kind: 'error', turnId: persistedTurnId }
                : terminalEventType === 'pending_client_tool'
                  ? { kind: 'pending_client_tool', turnId: persistedTurnId }
                  : { kind: 'success' };
            applyComposerTerminalOutcome(outcome);
            // Refresh the notebook file tree on every send-terminal outcome, not just success:
            // tools can register files before a turn ends in cancelled / error /
            // pending_client_tool too, and the handler's `complete` case only covers the
            // success + observer paths. (This is the send-side refresh the owner always had.)
            loadNotebookFiles().catch(error => {
              console.error('Failed to refresh notebook files after conversation turn:', error);
            });
            try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
          },
          controller.signal,
          {
            requestServerCancel: async () => {
              idleStopRequested = true;
              const turnId = streamTurn.current ?? getActiveStreamTurnId();
              if (turnId) {
                pendingStopRef.current = true;
                dispatch({ type: 'SET_CANCELLING', payload: true });
                requestServerStop(turnId);
              } else {
                reconcileLostSendAndStop(
                  content,
                  new Error('The conversation stream stopped sending data.'),
                );
              }
            },
          },
        );
      } catch (error: any) {
        if (sendStreamRef.current !== controller) {
          return;
        }
        if (error instanceof Error && (error.name === 'AbortError' || error.message.includes('aborted'))) {
          adoptSendStream(null);
          setActiveStreamTurnId(null);
          if (!pendingStopRef.current) {
            dispatch({ type: 'SET_CANCELLING', payload: false });
            clearSendStreamState();
          }
          return;
        }

        const persistedTurnId = streamTurn.current ?? sendStreamStateRef.current?.turnId;
        if (persistedTurnId) {
          pendingStopRef.current = true;
          dispatch({ type: 'SET_CANCELLING', payload: true });
          requestServerStop(persistedTurnId);
          return;
        }

        const isKnownPreflightFailure =
          (error.status === 409
            && (error.body?.code === 'ROUTING_MODEL_NOT_READY'
              || error.body?.code === 'OAUTH_RECONNECT_REQUIRED'
              || error.body?.runtimeStatus?.state))
          || (error?.status === 400 && isRuntimeNotReadyError(error));
        if (!isKnownPreflightFailure) {
          console.error('Send message failed', error);
          reconcileLostSendAndStop(
            content,
            error instanceof Error ? error : new Error('Chat request failed'),
          );
          return;
        }

        console.error('Send message failed', error);
        // Known preflight rejection: no draft restore either way (undo is the only
        // restore path) -- only decide the attachment chips from the persistence oracle.
        const preflightSnapshot = sendStreamStateRef.current?.snapshot;
        if (preflightSnapshot) {
          if (streamTurn.current) {
            dispatch({ type: 'CLEAR_ATTACHMENTS' });
          } else {
            dispatch({ type: 'SET_ATTACHMENTS', payload: preflightSnapshot.pendingAttachments });
          }
        }
        runtimeReadyCacheRef.current.clear();
        clearPendingStop();
        dispatch({ type: 'SET_STREAMING', payload: false });
        dispatch({ type: 'SET_CANCELLING', payload: false });
        dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
        dispatch({ type: 'CONVERT_STREAMING_IDS' });
        dispatch({ type: 'COMPLETE_STREAMING_TURN' });
        adoptSendStream(null);
        setActiveStreamTurnId(null);

        if (error.status === 409) {
          const body = error.body;

          if (body?.code === 'ROUTING_MODEL_NOT_READY') {
            const action = body.action || 'Open Settings and configure a default chat model.';
            dispatch({ type: 'SET_STREAMING_ERROR', payload: body.detail || 'No chat model is configured.' });
            showToast({
              type: 'error',
              title: 'No Chat Model',
              message: action,
              duration: 8000
            });
            try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
            return;
          }

          if (body?.code === 'OAUTH_RECONNECT_REQUIRED') {
            const providers: string[] = Array.isArray(body.providers) ? body.providers : [];
            const providerList = providers.length > 0 ? providers.join(', ') : 'one or more providers';
            const message = `Reconnect OAuth for ${providerList} before continuing.`;
            dispatch({ type: 'SET_STREAMING_ERROR', payload: message });
            showToast({
              type: 'error',
              title: 'Reconnect Required',
              message,
              duration: 8000
            });
            return;
          }

          const runtimeStatus = body?.runtimeStatus;
          if (runtimeStatus?.state && runtimeStatus.state !== 'ready') {
            dispatchRuntimeStatusWindowEvent(assistant?.id, runtimeStatus);
            if (runtimeStatus.state === 'failed' || runtimeStatus.state === 'invalid') {
              showToast({
                type: 'error',
                title: runtimeStatus.state === 'invalid' ? 'Incompatible Local Models' : 'Local Runtime Error',
                message: getRuntimeBlockingMessage(runtimeStatus)
              });
            }
            return;
          }
        }

        if (error?.status === 400 && isRuntimeNotReadyError(error)) {
          dispatchRuntimeStatusWindowEvent(assistant?.id, { state: 'requires_load' });
          return;
        }

        const errorMsg = error instanceof Error ? error.message : 'Chat request failed';
        dispatch({ type: 'SET_STREAMING_ERROR', payload: errorMsg });
        try { window.dispatchEvent(new Event('refresh-notebook-toolbar')); } catch {}
        showToast({
          type: 'error',
          title: 'Chat Request Failed',
          message: errorMsg
        });
        clearSendStreamState();
      }
    },
    [state._isUndoing, state.isStreaming, state._isCancelling, state.selectedAssistant, state.assistants, state.notebookTemplate, state.draftUserContent, projectId, notebookId, conversationId, handleStreamingEvent, state.pendingAttachments, loadNotebookFiles, showToast, abortObserverStream, adoptSendStream, applyComposerTerminalPolicy, clearPendingStop, clearSendStreamState, getActiveStreamTurnId, pendingStopRef, reconcileLostSendAndStop, refreshConversation, requestServerStop, runtimeReadyCacheRef, sendStreamRef, sendStreamStateRef, setActiveStreamTurnId]
  );

  const editAssistantMessage = useCallback(
    async (messageId: string, content: string) => {
      dispatch({ type: 'SET_EDIT_LOADING', payload: true });
      dispatch({ type: 'SET_EDIT_ERROR', payload: undefined });

      const originalMessage = state.messages.find(m => m.id === messageId);
      if (!originalMessage) {
        dispatch({ type: 'SET_EDIT_ERROR', payload: 'Message not found' });
        dispatch({ type: 'SET_EDIT_LOADING', payload: false });
        return;
      }

      let currentUser: any = null;
      try {
        currentUser = await userService.getCurrentUser();
        if (currentUser && currentUser.id && !state.userProfiles?.[currentUser.id]) {
          dispatch({
            type: 'SET_USER_PROFILES',
            payload: { [currentUser.id]: currentUser }
          });
        }
      } catch (error) {
        console.warn('Failed to get current user for optimistic update:', error);
      }

      const updatedMessage = {
        ...originalMessage,
        content,
        isEdited: true,
        lastEditedAt: new Date().toISOString(),
        userId: currentUser?.id,
        originalContent: originalMessage.originalContent || originalMessage.content,
      };
      dispatch({
        type: 'UPDATE_MESSAGE',
        payload: { id: messageId, updates: updatedMessage }
      });
      dispatch({ type: 'SET_EDITING', payload: undefined });

      try {
        await api.projects.notebooks.conversations.editMessage(projectId, notebookId, conversationId, messageId, content);
      } catch (err) {
        console.error('Edit message failed', err);
        dispatch({
          type: 'UPDATE_MESSAGE',
          payload: { id: messageId, updates: originalMessage }
        });
        dispatch({ type: 'SET_EDITING', payload: messageId });
        dispatch({ type: 'SET_EDIT_ERROR', payload: 'Failed to save message. Please try again.' });
      } finally {
        dispatch({ type: 'SET_EDIT_LOADING', payload: false });
      }
    },
    [projectId, notebookId, conversationId, state.messages, state.userProfiles]
  );

  const startEditingAssistant = useCallback((messageId: string) => {
    dispatch({ type: 'SET_EDITING', payload: messageId });
    dispatch({ type: 'SET_EDIT_ERROR', payload: undefined });
  }, []);

  const cancelEditingAssistant = useCallback(() => {
    dispatch({ type: 'SET_EDITING', payload: undefined });
    dispatch({ type: 'SET_EDIT_ERROR', payload: undefined });
    dispatch({ type: 'SET_EDIT_LOADING', payload: false });
  }, []);

  const setStreamingMode = useCallback(async (mode: StreamingMode, activeUser?: { userId: string; userName: string }) => {
    console.log('🔥 setStreamingMode called:', mode, activeUser);

    if (mode === 'observing') {
      console.log('🔥 Fetching user message for observer');
      try {
        const convo = await api.projects.notebooks.conversations.get(projectId, notebookId, conversationId);
        console.log('🔥 Fetched conversation:', convo?.messages?.length, 'messages');

        if (convo && convo.messages && convo.messages.length > 0) {
          console.log('🔥 Message roles:', convo.messages.map((m: any) => m.role));

          const lastUserMessage = convo.messages
            .filter((m: any) => m.role && m.role.toLowerCase() === 'user')
            .sort((a: any, b: any) => {
              const aTime = new Date(a.created || a.timestamp || a.createdAt || 0).getTime();
              const bTime = new Date(b.created || b.timestamp || b.createdAt || 0).getTime();
              return bTime - aTime;
            })[0];

          console.log('🔥 Last user message:', lastUserMessage);

          if (lastUserMessage) {
            console.log('🔥 Adding user message to conversation');
            dispatch({ type: 'ADD_MESSAGE', payload: lastUserMessage });
          } else {
            console.log('⚠️ No user message found in conversation');
          }
        }
      } catch (error) {
        console.error('Failed to fetch user message for observer:', error);
      }
    }

    console.log('🔥 Setting streaming mode to:', mode);
    dispatch({ type: 'SET_STREAMING_MODE', payload: { mode, activeUser } });
  }, [projectId, notebookId, conversationId, dispatch]);

  const reattachIfStreaming = useCallback(async (convo: {
    activeTurn?: { turnId: string; status: string; turnIndex: number } | null;
    lock?: { lockedByUserName: string } | null;
    streamingPreview?: { messageId: string; content: string; turnIndex: number } | null;
    assistantName?: string | null;
    messages?: MessageDto[];
  }) => {
    if (!convo.activeTurn
      || !['streaming', 'pending_client_tool'].includes((convo.activeTurn.status ?? '').toLowerCase())) {
      return;
    }

    if (sendStreamRef.current && !sendStreamRef.current.signal.aborted) {
      return;
    }

    if (observerStreamRef.current && !observerStreamRef.current.signal.aborted) {
      return;
    }

    const activeUser = convo.lock
      ? { userId: '', userName: convo.lock.lockedByUserName }
      : undefined;

    setActiveStreamTurnId(convo.activeTurn.turnId);
    dispatch({
      type: 'SET_STREAMING_MODE',
      payload: { mode: 'observing', activeUser },
    });

    if (convo.streamingPreview) {
      const previewId = `streaming-${convo.streamingPreview.messageId}`;
      const knownMessages = convo.messages ?? state.messages;
      const hasPreview = knownMessages.some(m => m.id === previewId);
      if (!hasPreview) {
        const placeholder: MessageDto = {
          id: previewId,
          role: 'assistant',
          content: convo.streamingPreview.content,
          created: new Date().toISOString(),
          isEdited: false,
          streaming: true,
          assistantName: convo.assistantName || state.selectedAssistant || undefined,
          turnIndex: convo.streamingPreview.turnIndex,
        } as MessageDto;
        dispatch({ type: 'ADD_MESSAGE', payload: placeholder });
      }
    }

    // Claim ownership synchronously before the fetch starts so a concurrent reattach cannot open a second socket.
    const controller = new AbortController();
    adoptObserverStream(controller);

    void api.projects.notebooks.conversations.observeConversationEvents(
      projectId,
      notebookId,
      conversationId,
      (event) => handleStreamingEvent(event, 'observer'),
      (error) => {
        if (error.name === 'AbortError') {
          return;
        }
        console.error('Observer stream error:', error);
        dispatch({ type: 'SET_STREAMING_ERROR', payload: error.message || 'Observer stream failed' });
      },
      () => {
        if (pendingStopRef.current) {
          return;
        }
        if (observerStreamRef.current === controller) {
          adoptObserverStream(null);
        }
        setActiveStreamTurnId(null);
        dispatch({ type: 'SET_CANCELLING', payload: false });
        clearPendingStop();
      },
      controller.signal,
    );
  }, [
    projectId,
    notebookId,
    conversationId,
    handleStreamingEvent,
    dispatch,
    setActiveStreamTurnId,
    adoptObserverStream,
    clearPendingStop,
    pendingStopRef,
    state.messages,
    state.selectedAssistant,
  ]);

  const cancelStream = useCallback(() => {
    dispatch({ type: 'SET_CANCELLING', payload: true });
    pendingStopRef.current = true;

    const turnId = getActiveStreamTurnId() ?? activeStreamTurnId;
    if (turnId) {
      requestServerStop(turnId);
    } else {
      if (stopReconcileTimerRef.current === null) {
        stopReconcileTimerRef.current = setTimeout(() => {
          stopReconcileTimerRef.current = null;
          reconcileLostSendAndStop(
            sendStreamStateRef.current?.snapshot?.draft ?? state.draftUserContent ?? '',
            new Error('The conversation stream did not provide a turn id.'),
          );
        }, 250);
      }
    }
  }, [
    activeStreamTurnId,
    dispatch,
    getActiveStreamTurnId,
    pendingStopRef,
    reconcileLostSendAndStop,
    requestServerStop,
    sendStreamStateRef,
    state.draftUserContent,
  ]);

  const undoLastTurn = useCallback(async () => {
    if (state._isUndoing || state.isStreaming || state._isCancelling || pendingStopRef.current) {
      return;
    }

    const hasUserMessage = state.messages.some(m => (m.role || '').toLowerCase() === 'user');
    if (!hasUserMessage) {
      console.warn('No user messages to undo');
      return;
    }

    // Finalize any in-flight streaming state first; COMPLETE_STREAMING_TURN must run before we
    // flag the undo as in-progress so it can't clear the flag we are about to set.
    dispatch({ type: 'FINALIZE_STREAMING_MESSAGE', payload: {} });
    dispatch({ type: 'CONVERT_STREAMING_IDS' });
    dispatch({ type: 'COMPLETE_STREAMING_TURN' });

    dispatch({ type: 'SET_UNDOING', payload: true });

    const originalMessages = [...state.messages];
    const previousDraft = state.draftUserContent || '';
    const previousAttachments = [...(state.pendingAttachments ?? [])];

    const lastUserMessage = [...state.messages]
      .slice()
      .reverse()
      .find(m => (m.role || '').toLowerCase() === 'user');
    const undoneUserContent = (lastUserMessage?.content ?? '').toString();
    const undoneAttachments = (lastUserMessage?.attachments ?? [])
      .map(attachmentDtoToPendingAttachment)
      .filter((attachment): attachment is PendingAttachment => attachment !== null);

    dispatch({ type: 'REMOVE_LAST_TURN' });
    dispatch({ type: 'SET_DRAFT', payload: undoneUserContent });
    dispatch({ type: 'SET_ATTACHMENTS', payload: undoneAttachments });

    try {
      await api.projects.notebooks.conversations.undoLast(projectId, notebookId, conversationId);
    } catch (err: any) {
      console.error('Undo turn failed', err);
      dispatch({ type: 'SET_MESSAGES', payload: originalMessages });
      dispatch({ type: 'SET_DRAFT', payload: previousDraft });
      dispatch({ type: 'SET_ATTACHMENTS', payload: previousAttachments });
      try {
        const serverConversation = await api.projects.notebooks.conversations.get(
          projectId,
          notebookId,
          conversationId,
        );
        if ((serverConversation?.activeTurn
          && ['streaming', 'pending_client_tool'].includes((serverConversation.activeTurn.status ?? '').toLowerCase()))
          || serverConversation?.lock
          || serverConversation?.streamingPreview) {
          await refreshConversation({ force: true });
        } else {
          dispatch({ type: 'SET_STREAMING', payload: false });
          dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'at-rest' } });
        }
      } catch (reconcileError) {
        // Do not unlock after a failed undo when the server state cannot be confirmed.
        dispatch({ type: 'SET_STREAMING', payload: true });
        dispatch({ type: 'SET_STREAMING_MODE', payload: { mode: 'observing' } });
        console.warn('Failed to reconcile conversation after undo failure:', reconcileError);
      }
      showToast({
        type: 'error',
        title: 'Undo Failed',
        message: err?.status === 409
          ? 'The conversation is busy right now. Wait for the current response to finish and try again.'
          : 'Could not undo the last message. Please try again.'
      });
    } finally {
      dispatch({ type: 'SET_UNDOING', payload: false });
    }
  }, [projectId, notebookId, conversationId, state.messages, state._isUndoing, state.isStreaming, state._isCancelling, state.draftUserContent, state.pendingAttachments, refreshConversation, showToast]);

  const setSelectedAssistant = useCallback(async (name: string) => {
    dispatch({ type: 'SET_ASSISTANT', payload: name });

    const assistant = assistantByName[name];
    if (assistant?.id) {
      checkRuntimeStatus(projectId, notebookId, assistant.id, inflightRuntimeChecksRef.current, runtimeReadyCacheRef.current).catch(() => {});
    }
  }, [projectId, notebookId, assistantByName]);

  const setDraftUserContent = useCallback((text: string) => dispatch({ type: 'SET_DRAFT', payload: text }), []);

  const addPendingAttachment = useCallback((att: PendingAttachment) => {
    dispatch({ type: 'ADD_ATTACHMENT', payload: att });
  }, []);

  const removePendingAttachment = useCallback((fileId: string) => {
    dispatch({ type: 'REMOVE_ATTACHMENT', payload: fileId });
  }, []);

  return {
    sendMessage,
    editAssistantMessage,
    startEditingAssistant,
    cancelEditingAssistant,
    setStreamingMode,
    reattachIfStreaming,
    onTurnIdAssigned,
    clearPendingStop,
    cancelStream,
    undoLastTurn,
    setSelectedAssistant,
    setDraftUserContent,
    addPendingAttachment,
    removePendingAttachment,
  };
}
