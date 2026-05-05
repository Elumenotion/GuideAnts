export interface LocalOperationLike {
  operationId: string;
  status?: string | null;
  error?: string | null;
}

export interface LocalDownloadOperationState {
  serviceId: string;
  operationId: string;
  status: string;
  inFlight: boolean;
  error?: string | null;
}

export interface LocalRuntimeReadinessState {
  serviceId: string;
  ready: boolean;
  status: string;
  detail?: string | null;
}

export const LOCAL_OPERATION_POLL_INTERVAL_MS = 2000;
export const LOCAL_OPERATION_POLL_FAILURE_THRESHOLD = 5;
export const LOCAL_OPERATION_UNREACHABLE_MESSAGE =
  'Download status is no longer reachable. The runtime container may have restarted.';

export function normalizeOperationStatus(value: string | null | undefined): string {
  return (value ?? '').trim().toLowerCase();
}

export function isOperationTerminalStatus(value: string | null | undefined): boolean {
  const status = normalizeOperationStatus(value);
  return status === 'completed'
    || status === 'failed'
    || status === 'error'
    || status === 'cancelled'
    || status === 'canceled';
}

export function isOperationFailedStatus(value: string | null | undefined): boolean {
  const status = normalizeOperationStatus(value);
  return status === 'failed'
    || status === 'error'
    || status === 'cancelled'
    || status === 'canceled';
}

export function isOperationInFlight(value: string | null | undefined): boolean {
  return !isOperationTerminalStatus(value);
}

interface StartLocalOperationPollOptions<T extends LocalOperationLike> {
  poll: () => Promise<T>;
  onUpdate: (operation: T) => void;
  onTerminal?: (operation: T) => void;
  onPollFailureThreshold?: () => void;
  intervalMs?: number;
  failureThreshold?: number;
}

export function startLocalOperationPoll<T extends LocalOperationLike>({
  poll,
  onUpdate,
  onTerminal,
  onPollFailureThreshold,
  intervalMs = LOCAL_OPERATION_POLL_INTERVAL_MS,
  failureThreshold = LOCAL_OPERATION_POLL_FAILURE_THRESHOLD,
}: StartLocalOperationPollOptions<T>): number {
  let consecutivePollFailures = 0;
  return window.setInterval(() => {
    void (async () => {
      try {
        const operation = await poll();
        consecutivePollFailures = 0;
        onUpdate(operation);
        if (isOperationTerminalStatus(operation.status)) {
          onTerminal?.(operation);
        }
      } catch {
        consecutivePollFailures += 1;
        if (consecutivePollFailures >= failureThreshold) {
          onPollFailureThreshold?.();
        }
      }
    })();
  }, intervalMs);
}
