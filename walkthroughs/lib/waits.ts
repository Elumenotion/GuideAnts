import type { Page } from '@playwright/test';

import type { Timeline } from './timeline.js';

export interface PauseOptions {
  ms: number;
  reason: string;
  planned?: boolean;
}

/** Mark an explicit idle segment (narration gap, deliberate hold). */
export async function pause(
  timeline: Timeline,
  options: PauseOptions,
): Promise<void> {
  const planned = options.planned ?? true;
  await timeline.emit({
    kind: 'idle.start',
    reason: options.reason,
    planned_ms: options.ms,
    planned,
  });
  await new Promise((resolve) => setTimeout(resolve, options.ms));
  await timeline.emit({
    kind: 'idle.end',
    reason: options.reason,
    planned,
  });
}

/** Wait for network to go quiet, but don't block forever on polling sockets. */
export async function waitForNetworkSettled(
  page: Page,
  timeoutMs = 8_000,
): Promise<void> {
  await page.waitForLoadState('networkidle', { timeout: timeoutMs }).catch(() => undefined);
}
