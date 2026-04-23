import React, { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import LoadingSpinner from '../../LoadingSpinner';

/**
 * Shown when a chat turn ends with an SSE error whose `code` indicates the local llama runtime
 * has crashed (typically CUDA OOM). This is the first step of the crash-recovery flow:
 *
 *   (1) LlamaCrashedModal  — user acknowledges the crash and clicks "Restart service"
 *   (2) /llama-runtime/restart is invoked, we wait for the spinner
 *   (3) on success, we dispatch `llama-runtime-requires-load` so NotebookDetails opens the
 *       existing LlamaRuntimeModal and the user explicitly re-loads the model
 *
 * No automatic re-send of the failed chat turn; OOMs tend to recur on the same prompt.
 */

export type LlamaCrashReason = 'OutOfMemory' | 'Crashed' | string;

export interface LlamaCrashedModalProps {
  isOpen: boolean;
  reason: LlamaCrashReason;
  upstreamDetail?: string | null;
  onClose: () => void;
  onRestart: () => Promise<void>;
  onAfterRestart: () => void;
}

const COPY_BY_REASON: Record<string, { title: string; body: string }> = {
  OutOfMemory: {
    title: 'Local model ran out of GPU memory',
    body:
      'The local model couldn\'t fit this request into VRAM and the runtime has to be restarted before it can respond again. ' +
      'Try reducing the context size, closing other GPU workloads, or switching to a smaller model after the restart.'
  },
  Crashed: {
    title: 'Local model runtime crashed',
    body:
      'The local model stopped responding mid-reply. The runtime has to be restarted before it can respond again.'
  }
};

function copyFor(reason: LlamaCrashReason) {
  return COPY_BY_REASON[reason] ?? COPY_BY_REASON.Crashed;
}

export const LlamaCrashedModal: React.FC<LlamaCrashedModalProps> = ({
  isOpen,
  reason,
  upstreamDetail,
  onClose,
  onRestart,
  onAfterRestart
}) => {
  const [isRestarting, setIsRestarting] = useState(false);
  const [restartError, setRestartError] = useState<string | null>(null);

  // Reset transient restart state when the modal closes so a *second* crash in the same session
  // doesn't inherit a stale "Restart failed" banner or a stuck spinner from the previous attempt.
  // Keyed off isOpen rather than a mount-key because the parent toggles this component in place.
  useEffect(() => {
    if (!isOpen) {
      setIsRestarting(false);
      setRestartError(null);
    }
  }, [isOpen]);

  if (!isOpen) return null;

  const { title, body } = copyFor(reason);

  // Local click handler — used inline in onClick only and never passed downward, so there is
  // no value in memoizing it with useCallback; doing so would just add a dep array to maintain.
  const handleRestartClick = async () => {
    setIsRestarting(true);
    setRestartError(null);
    try {
      await onRestart();
      onAfterRestart();
    } catch (err: any) {
      const msg =
        err?.message ||
        'Failed to restart the local model runtime. Check the container logs and try again.';
      setRestartError(msg);
    } finally {
      setIsRestarting(false);
    }
  };

  const markup = (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
      <div
        className="bg-white rounded-lg shadow-xl w-full max-w-md mx-4"
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="llama-crashed-modal-title"
      >
        <div className="flex flex-col p-6 pb-4">
          <h2 id="llama-crashed-modal-title" className="text-lg font-semibold text-red-700">
            {title}
          </h2>
          <p className="text-sm text-gray-700 mt-2">{body}</p>
          {upstreamDetail ? (
            <details className="mt-3 text-xs text-gray-500">
              <summary className="cursor-pointer">Technical details</summary>
              <pre className="mt-2 whitespace-pre-wrap break-words bg-gray-50 border border-gray-200 rounded p-2 max-h-48 overflow-auto">
                {upstreamDetail}
              </pre>
            </details>
          ) : null}
        </div>

        {restartError ? (
          <div className="mx-6 mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">
            {restartError}
          </div>
        ) : null}

        <div className="px-6 pb-6">
          {isRestarting ? (
            <div className="flex flex-col items-center justify-center space-y-3 py-6">
              <LoadingSpinner />
              <p className="text-sm text-slate-500">Restarting local model service…</p>
            </div>
          ) : null}
        </div>

        <div className="px-6 py-4 border-t border-gray-200 flex justify-end space-x-3">
          <button
            type="button"
            onClick={onClose}
            disabled={isRestarting}
            className="px-4 py-2 text-sm border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
          >
            Dismiss
          </button>
          <button
            type="button"
            onClick={handleRestartClick}
            disabled={isRestarting}
            className="px-4 py-2 text-sm rounded-md text-white bg-red-600 hover:bg-red-700 disabled:opacity-50"
          >
            {restartError ? 'Try again' : 'Restart service'}
          </button>
        </div>
      </div>
    </div>
  );

  return createPortal(markup, document.body);
};
