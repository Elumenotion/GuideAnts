import type { Page } from '@playwright/test';

import type { Timeline } from './timeline.js';

function monitorBounds():
  | { left: number; top: number; width: number; height: number }
  | undefined {
  const left = Number(process.env.WALKTHROUGH_MONITOR_LEFT);
  const top = Number(process.env.WALKTHROUGH_MONITOR_TOP);
  const width = Number(process.env.WALKTHROUGH_MONITOR_WIDTH);
  const height = Number(process.env.WALKTHROUGH_MONITOR_HEIGHT);
  if (
    !Number.isFinite(left) ||
    !Number.isFinite(top) ||
    !Number.isFinite(width) ||
    !Number.isFinite(height) ||
    width <= 0 ||
    height <= 0
  ) {
    return undefined;
  }
  return { left, top, width, height };
}

/** Size Chrome to the full capture monitor (preferred) or maximize as fallback. */
export async function prepareChromeWindow(
  page: Page,
  timeline: Timeline,
): Promise<void> {
  const client = await page.context().newCDPSession(page);
  const { windowId } = await client.send('Browser.getWindowForTarget');
  const bounds = monitorBounds();

  if (bounds) {
    await client.send('Browser.setWindowBounds', {
      windowId,
      bounds: {
        left: bounds.left,
        top: bounds.top,
        width: bounds.width,
        height: bounds.height,
        windowState: 'normal',
      },
    });
    await timeline.emit({
      kind: 'note',
      message: 'chrome.window.monitor_bounds',
      ...bounds,
    });
  } else {
    await client.send('Browser.setWindowBounds', {
      windowId,
      bounds: { windowState: 'maximized' },
    });
    await timeline.emit({
      kind: 'note',
      message: 'chrome.window.maximized',
    });
  }

  await page.waitForTimeout(400);
}
