import { useEffect } from 'react';
import { api } from '../../services/api';
import type { ModelDownloadOperationDto } from '../../types/settings';
import { isLocalModelOnboardingTerminal } from './status';

const DEFAULT_POLL_INTERVAL_MS = 2000;
const DEFAULT_FAILURE_THRESHOLD = 5;

interface CreateLocalModelOnboardingPollerOptions {
  operationId: string;
  onUpdate: (operation: ModelDownloadOperationDto) => void;
  onTerminal?: (operation: ModelDownloadOperationDto) => void;
  onPollFailureThreshold?: () => void;
  intervalMs?: number;
  failureThreshold?: number;
}

export function createLocalModelOnboardingPoller({
  operationId,
  onUpdate,
  onTerminal,
  onPollFailureThreshold,
  intervalMs = DEFAULT_POLL_INTERVAL_MS,
  failureThreshold = DEFAULT_FAILURE_THRESHOLD,
}: CreateLocalModelOnboardingPollerOptions): number {
  let consecutivePollFailures = 0;

  return window.setInterval(() => {
    void (async () => {
      try {
        const operation = await api.settings.getDownloadStatus(operationId);
        consecutivePollFailures = 0;
        onUpdate(operation);
        if (isLocalModelOnboardingTerminal(operation.status)) {
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

interface UseLocalModelOnboardingOperationOptions {
  operationId: string | null;
  enabled?: boolean;
  onUpdate: (operation: ModelDownloadOperationDto) => void;
  onTerminal?: (operation: ModelDownloadOperationDto) => void;
  onPollFailureThreshold?: () => void;
  intervalMs?: number;
  failureThreshold?: number;
}

export function useLocalModelOnboardingOperation({
  operationId,
  enabled = true,
  onUpdate,
  onTerminal,
  onPollFailureThreshold,
  intervalMs,
  failureThreshold,
}: UseLocalModelOnboardingOperationOptions): void {
  useEffect(() => {
    if (!operationId || !enabled) {
      return;
    }

    const timerId = createLocalModelOnboardingPoller({
      operationId,
      onUpdate,
      onTerminal,
      onPollFailureThreshold,
      intervalMs,
      failureThreshold,
    });

    return () => {
      window.clearInterval(timerId);
    };
  }, [enabled, failureThreshold, intervalMs, onPollFailureThreshold, onTerminal, onUpdate, operationId]);
}
