import { useEffect } from 'react';
import { api } from '../../services/api';
import type { LlamaOperationStatusDto, ModelDownloadOperationDto } from '../../types/settings';
import { mapLlamaOperationStatusToDownloadDto } from './mapOperationStatus';
import { isLocalModelOnboardingTerminal } from './status';

const DEFAULT_POLL_INTERVAL_MS = 2000;
const DEFAULT_FAILURE_THRESHOLD = 5;

export type LocalModelOnboardingPollRoute = 'operations' | 'downloads';

interface CreateLocalModelOnboardingPollerOptions {
  operationId: string;
  pollRoute?: LocalModelOnboardingPollRoute;
  onUpdate: (operation: ModelDownloadOperationDto) => void;
  onTerminal?: (operation: ModelDownloadOperationDto) => void;
  onPollFailureThreshold?: () => void;
  intervalMs?: number;
  failureThreshold?: number;
}

async function fetchOperationStatus(
  operationId: string,
  pollRoute: LocalModelOnboardingPollRoute
): Promise<ModelDownloadOperationDto> {
  if (pollRoute === 'operations') {
    const operation = await api.settings.getLlamaOperationStatus(operationId);
    return mapLlamaOperationStatusToDownloadDto(operation);
  }
  return api.settings.getDownloadStatus(operationId);
}

export function createLocalModelOnboardingPoller({
  operationId,
  pollRoute = 'downloads',
  onUpdate,
  onTerminal,
  onPollFailureThreshold,
  intervalMs = DEFAULT_POLL_INTERVAL_MS,
  failureThreshold = DEFAULT_FAILURE_THRESHOLD,
}: CreateLocalModelOnboardingPollerOptions): number {
  let consecutivePollFailures = 0;
  let stopped = false;
  let timerId = 0;

  timerId = window.setInterval(() => {
    void (async () => {
      if (stopped) {
        return;
      }
      try {
        const operation = await fetchOperationStatus(operationId, pollRoute);
        consecutivePollFailures = 0;
        if (isLocalModelOnboardingTerminal(operation.status)) {
          // Stop before invoking callbacks so a terminal status fires onUpdate/onTerminal
          // exactly once. Otherwise the interval keeps re-firing terminal side effects
          // (e.g. catalog refetch) every tick.
          stopped = true;
          window.clearInterval(timerId);
          onUpdate(operation);
          onTerminal?.(operation);
          return;
        }
        onUpdate(operation);
      } catch {
        consecutivePollFailures += 1;
        if (consecutivePollFailures >= failureThreshold) {
          onPollFailureThreshold?.();
        }
      }
    })();
  }, intervalMs);

  return timerId;
}

interface UseLocalModelOnboardingOperationOptions {
  operationId: string | null;
  enabled?: boolean;
  pollRoute?: LocalModelOnboardingPollRoute;
  onUpdate: (operation: ModelDownloadOperationDto) => void;
  onTerminal?: (operation: ModelDownloadOperationDto) => void;
  onPollFailureThreshold?: () => void;
  intervalMs?: number;
  failureThreshold?: number;
}

export function useLocalModelOnboardingOperation({
  operationId,
  enabled = true,
  pollRoute = 'downloads',
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
      pollRoute,
      onUpdate,
      onTerminal,
      onPollFailureThreshold,
      intervalMs,
      failureThreshold,
    });

    return () => {
      window.clearInterval(timerId);
    };
  }, [enabled, failureThreshold, intervalMs, onPollFailureThreshold, onTerminal, onUpdate, operationId, pollRoute]);
}

interface UseCuratedOperationPollingOptions {
  operationId: string | null;
  enabled?: boolean;
  onUpdate: (operation: LlamaOperationStatusDto) => void;
  onTerminal?: (operation: LlamaOperationStatusDto) => void;
  onPollFailureThreshold?: () => void;
  intervalMs?: number;
  failureThreshold?: number;
}

export function useCuratedOperationPolling({
  operationId,
  enabled = true,
  onUpdate,
  onTerminal,
  onPollFailureThreshold,
  intervalMs,
  failureThreshold,
}: UseCuratedOperationPollingOptions): void {
  useEffect(() => {
    if (!operationId || !enabled) {
      return;
    }

    let consecutivePollFailures = 0;
    let stopped = false;
    let timerId = 0;
    timerId = window.setInterval(() => {
      void (async () => {
        if (stopped) {
          return;
        }
        try {
          const operation = await api.settings.getLlamaOperationStatus(operationId);
          consecutivePollFailures = 0;
          if (isLocalModelOnboardingTerminal(operation.status)) {
            stopped = true;
            window.clearInterval(timerId);
            onUpdate(operation);
            onTerminal?.(operation);
            return;
          }
          onUpdate(operation);
        } catch {
          consecutivePollFailures += 1;
          if (consecutivePollFailures >= (failureThreshold ?? DEFAULT_FAILURE_THRESHOLD)) {
            onPollFailureThreshold?.();
          }
        }
      })();
    }, intervalMs ?? DEFAULT_POLL_INTERVAL_MS);

    return () => {
      stopped = true;
      window.clearInterval(timerId);
    };
  }, [enabled, failureThreshold, intervalMs, onPollFailureThreshold, onTerminal, onUpdate, operationId]);
}
