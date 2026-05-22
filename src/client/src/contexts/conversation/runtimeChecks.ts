import { api } from '../../services/api';

export const dispatchRuntimeStatusWindowEvent = (assistantId: string | undefined, runtimeStatus: any) => {
  if (!runtimeStatus?.state || runtimeStatus.state === 'ready') {
    return;
  }

  if (runtimeStatus.state === 'loading') {
    window.dispatchEvent(new CustomEvent('llama-runtime-loading', { detail: { operation: runtimeStatus.activeOperation } }));
    return;
  }

  window.dispatchEvent(new CustomEvent('llama-runtime-requires-load', { detail: { assistantId, runtimeStatus } }));
};

export const getRuntimeBlockingMessage = (runtimeStatus: any): string => {
  return runtimeStatus?.activeOperation?.errorDetails
    || runtimeStatus?.conflicts?.[0]
    || 'Local model runtime is not ready.';
};

const runtimeReadyCacheByNotebook = new Map<string, Set<string>>();

export const getNotebookRuntimeReadyCache = (notebookId: string): Set<string> => {
  let cache = runtimeReadyCacheByNotebook.get(notebookId);
  if (!cache) {
    cache = new Set<string>();
    runtimeReadyCacheByNotebook.set(notebookId, cache);
  }
  return cache;
};

export const clearNotebookRuntimeReadyCache = (notebookId: string): void => {
  const cache = runtimeReadyCacheByNotebook.get(notebookId);
  cache?.clear();
};

/**
 * Checks llama runtime status for a given assistant and dispatches
 * window events so the runtime modal can react.
 *
 * Returns the runtime status, or null if the check was skipped (dedup).
 * Callers that need to gate on the result (e.g. sendMessage preflight)
 * should inspect the returned status.
 */
export async function checkRuntimeStatus(
  projectId: string,
  notebookId: string,
  assistantId: string,
  inflightChecks: Set<string>,
  readyCache?: Set<string>,
): Promise<{ state: string; activeOperation?: any } | null> {
  if (readyCache?.has(assistantId)) return { state: 'ready' };
  const checkKey = `${notebookId}-${assistantId}`;
  if (inflightChecks.has(checkKey)) return null;

  inflightChecks.add(checkKey);
  try {
    const runtimeStatus = await api.projects.notebooks.conversations.checkLlamaRuntime(projectId, notebookId, assistantId);
    if (runtimeStatus.state === 'ready') {
      readyCache?.add(assistantId);
    } else {
      dispatchRuntimeStatusWindowEvent(assistantId, runtimeStatus);
    }
    return runtimeStatus;
  } catch (error: any) {
    if (error.status === 409) {
      const runtimeStatus = error.body?.runtimeStatus;
      if (runtimeStatus?.state) {
        dispatchRuntimeStatusWindowEvent(assistantId, runtimeStatus);
      }
      return runtimeStatus ?? { state: 'failed', conflicts: [error?.message || 'Local runtime is not ready.'] };
    }
    throw error;
  } finally {
    inflightChecks.delete(checkKey);
  }
}
