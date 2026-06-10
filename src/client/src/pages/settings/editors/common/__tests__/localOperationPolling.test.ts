import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  isOperationFailedStatus,
  isOperationInFlight,
  isOperationTerminalStatus,
  LOCAL_OPERATION_POLL_FAILURE_THRESHOLD,
  LOCAL_OPERATION_POLL_INTERVAL_MS,
  LOCAL_OPERATION_UNREACHABLE_MESSAGE,
  normalizeOperationStatus,
  startLocalOperationPoll,
} from '../localOperationPolling';

describe('localOperationPolling', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  describe('status helpers', () => {
    it('normalizes operation status strings', () => {
      expect(normalizeOperationStatus(' Completed ')).toBe('completed');
      expect(normalizeOperationStatus(undefined)).toBe('');
    });

    it('detects terminal statuses', () => {
      expect(isOperationTerminalStatus('completed')).toBe(true);
      expect(isOperationTerminalStatus('failed')).toBe(true);
      expect(isOperationTerminalStatus('error')).toBe(true);
      expect(isOperationTerminalStatus('cancelled')).toBe(true);
      expect(isOperationTerminalStatus('running')).toBe(false);
    });

    it('detects failed statuses', () => {
      expect(isOperationFailedStatus('failed')).toBe(true);
      expect(isOperationFailedStatus('canceled')).toBe(true);
      expect(isOperationFailedStatus('completed')).toBe(false);
    });

    it('treats non-terminal statuses as in flight', () => {
      expect(isOperationInFlight('queued')).toBe(true);
      expect(isOperationInFlight('running')).toBe(true);
      expect(isOperationInFlight('completed')).toBe(false);
    });

    it('exports stable polling constants', () => {
      expect(LOCAL_OPERATION_POLL_INTERVAL_MS).toBe(2000);
      expect(LOCAL_OPERATION_POLL_FAILURE_THRESHOLD).toBe(5);
      expect(LOCAL_OPERATION_UNREACHABLE_MESSAGE).toContain('no longer reachable');
    });
  });

  describe('startLocalOperationPoll', () => {
    it('polls and forwards operation updates', async () => {
      const poll = vi.fn().mockResolvedValue({ operationId: 'op-1', status: 'running' });
      const onUpdate = vi.fn();

      const timerId = startLocalOperationPoll({
        poll,
        onUpdate,
        intervalMs: 1000,
      });

      await vi.advanceTimersByTimeAsync(1000);

      expect(poll).toHaveBeenCalledTimes(1);
      expect(onUpdate).toHaveBeenCalledWith({ operationId: 'op-1', status: 'running' });

      window.clearInterval(timerId);
    });

    it('invokes onTerminal for completed operations', async () => {
      const poll = vi.fn().mockResolvedValue({ operationId: 'op-2', status: 'completed' });
      const onUpdate = vi.fn();
      const onTerminal = vi.fn();

      const timerId = startLocalOperationPoll({
        poll,
        onUpdate,
        onTerminal,
        intervalMs: 500,
      });

      await vi.advanceTimersByTimeAsync(500);

      expect(onTerminal).toHaveBeenCalledWith({ operationId: 'op-2', status: 'completed' });
      window.clearInterval(timerId);
    });

    it('invokes onPollFailureThreshold after repeated poll failures', async () => {
      const poll = vi.fn().mockRejectedValue(new Error('network'));
      const onUpdate = vi.fn();
      const onPollFailureThreshold = vi.fn();

      const timerId = startLocalOperationPoll({
        poll,
        onUpdate,
        onPollFailureThreshold,
        intervalMs: 100,
        failureThreshold: 3,
      });

      await vi.advanceTimersByTimeAsync(300);

      expect(onPollFailureThreshold).toHaveBeenCalledTimes(1);
      expect(onUpdate).not.toHaveBeenCalled();
      window.clearInterval(timerId);
    });

    it('resets failure counter after a successful poll', async () => {
      const poll = vi
        .fn()
        .mockRejectedValueOnce(new Error('temporary'))
        .mockRejectedValueOnce(new Error('temporary'))
        .mockResolvedValueOnce({ operationId: 'op-3', status: 'running' });
      const onPollFailureThreshold = vi.fn();

      const timerId = startLocalOperationPoll({
        poll,
        onUpdate: vi.fn(),
        onPollFailureThreshold,
        intervalMs: 100,
        failureThreshold: 3,
      });

      await vi.advanceTimersByTimeAsync(300);

      expect(onPollFailureThreshold).not.toHaveBeenCalled();
      window.clearInterval(timerId);
    });
  });
});
