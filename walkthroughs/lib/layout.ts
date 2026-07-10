import type { Page } from '@playwright/test';

const DEFAULT_SIDEBAR_WIDTH = Number(
  process.env.WALKTHROUGH_SIDEBAR_WIDTH ?? '520',
);

const LAYOUT_KEYS = {
  notebook: 'notebookSidebarWidth',
  project: 'sidebarWidth',
} as const;

/** Widen notebook/project sidebars before the app reads localStorage on load. */
export async function registerWalkthroughLayout(page: Page): Promise<void> {
  const width = DEFAULT_SIDEBAR_WIDTH;
  await page.context().addInitScript((sidebarWidth: number) => {
    localStorage.setItem('notebookSidebarWidth', String(sidebarWidth));
    localStorage.setItem('sidebarWidth', String(sidebarWidth));
    localStorage.setItem('notebookSidebarCollapsed', 'false');
    localStorage.setItem('sidebarCollapsed', 'false');
  }, width);
}

export function walkthroughSidebarWidth(): number {
  return DEFAULT_SIDEBAR_WIDTH;
}

/**
 * Ensure the notebook page picked up the walkthrough sidebar width.
 * Reloads once if localStorage was stale for the current document.
 */
export async function ensureWalkthroughLayout(page: Page): Promise<void> {
  const width = DEFAULT_SIDEBAR_WIDTH;
  const reloaded = await page.evaluate((sidebarWidth) => {
    const current = localStorage.getItem('notebookSidebarWidth');
    if (current === String(sidebarWidth)) {
      return false;
    }
    localStorage.setItem('notebookSidebarWidth', String(sidebarWidth));
    localStorage.setItem('sidebarWidth', String(sidebarWidth));
    localStorage.setItem('notebookSidebarCollapsed', 'false');
    localStorage.setItem('sidebarCollapsed', 'false');
    window.location.reload();
    return true;
  }, width);

  if (reloaded) {
    await page.waitForLoadState('domcontentloaded');
  }
}
