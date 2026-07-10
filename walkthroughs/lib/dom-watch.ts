import type { Page } from '@playwright/test';

import type { Timeline } from './timeline.js';

/** Observe DOM mutations and emit timeline events for unplanned UI activity. */
export async function installDomWatch(
  page: Page,
  timeline: Timeline,
): Promise<() => Promise<void>> {
  await page.exposeFunction('__walkthroughDomMutation', (summary: string) => {
    void timeline.emit({
      kind: 'dom.mutation',
      summary,
    });
  });

  await page.evaluate(() => {
    const OVERLAY_ROOT_IDS = new Set(['pw-tutorial-overlay', 'pw-dom-watch']);

    function isOverlayMutation(node: Node): boolean {
      let current: Node | null = node;
      while (current) {
        if (current instanceof Element && OVERLAY_ROOT_IDS.has(current.id)) {
          return true;
        }
        current = current.parentNode;
      }
      return false;
    }

    const existing = document.getElementById('pw-dom-watch');
    existing?.remove();

    let pending = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const observer = new MutationObserver((records) => {
      let added = 0;
      let removed = 0;
      let attrs = 0;

      for (const record of records) {
        if (record.type === 'attributes') {
          if (isOverlayMutation(record.target)) {
            continue;
          }
          attrs += 1;
          continue;
        }

        for (const node of record.addedNodes) {
          if (!isOverlayMutation(node)) {
            added += 1;
          }
        }
        for (const node of record.removedNodes) {
          if (!isOverlayMutation(node)) {
            removed += 1;
          }
        }
      }

      pending += added + removed + attrs;
      if (pending === 0) {
        return;
      }

      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        const count = pending;
        pending = 0;
        if (count === 0) return;
        void window.__walkthroughDomMutation?.(`nodes+${count}`);
      }, 120);
    });

    observer.observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      characterData: true,
    });

    const marker = document.createElement('meta');
    marker.id = 'pw-dom-watch';
    marker.dataset.observer = 'active';
    document.head.appendChild(marker);

    window.__walkthroughDomWatchCleanup = () => {
      observer.disconnect();
      marker.remove();
      if (timer) clearTimeout(timer);
    };
  });

  return async () => {
    try {
      await page.evaluate(() => window.__walkthroughDomWatchCleanup?.());
    } catch {
      // Browser may already be closed after a timeout.
    }
  };
}

declare global {
  interface Window {
    __walkthroughDomMutation?: (summary: string) => void;
    __walkthroughDomWatchCleanup?: () => void;
  }
}
