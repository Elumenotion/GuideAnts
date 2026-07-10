import { test as base, expect } from '@playwright/test';

import { signIn } from '../lib/auth.js';
import { installDomWatch } from '../lib/dom-watch.js';
import { registerWalkthroughLayout } from '../lib/layout.js';
import { TutorialPointer } from '../lib/pointer.js';
import { Timeline } from '../lib/timeline.js';
import { walkthroughMode } from '../lib/clock.js';
import { prepareChromeWindow } from '../lib/window.js';

type WalkthroughFixtures = {
  timeline: Timeline;
  pointer: TutorialPointer;
  /** Fresh sign-in before the scenario body. */
  signedIn: void;
};

export const test = base.extend<WalkthroughFixtures>({
  timeline: async ({}, use, testInfo) => {
    const timeline = new Timeline(testInfo);
    await timeline.startScenario({ mode: walkthroughMode() });
    await use(timeline);
    const status = testInfo.status === 'passed' ? 'pass' : 'fail';
    await timeline.endScenario(status, {
      errors: testInfo.errors.map((e) => e.message),
    });
    timeline.writeManifest();
  },

  pointer: async ({ page, timeline }, use) => {
    const pointer = new TutorialPointer(page, timeline);
    await registerWalkthroughLayout(page);
    await pointer.registerInitScript();
    await use(pointer);
    await pointer.remove();
  },

  signedIn: async ({ page, timeline, pointer: _pointer }, use) => {
    await prepareChromeWindow(page, timeline);
    await signIn(page, timeline);
    await use();
  },
});

export { expect, signIn };

export async function withDomWatch(
  page: import('@playwright/test').Page,
  timeline: Timeline,
  run: () => Promise<void>,
): Promise<void> {
  const cleanup = await installDomWatch(page, timeline);
  try {
    await run();
  } finally {
    await cleanup();
  }
}

export function notebookPath(): string {
  return (
    process.env.WALKTHROUGH_NOTEBOOK_PATH ??
    '/projects/364d7bcc-9d6d-458b-9761-2e484a328fba/notebooks/398ef293-e5bd-4963-b596-ae0280f6fa45'
  );
}
