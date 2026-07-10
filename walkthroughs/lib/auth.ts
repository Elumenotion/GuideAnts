import type { Page } from '@playwright/test';

import type { Timeline } from './timeline.js';
import { typeWithEvents } from './typing.js';

export interface SignInOptions {
  /** Path to open after login, e.g. `/projects/.../notebooks/...` */
  returnPath?: string;
}

export function walkthroughEmail(): string {
  return process.env.WALKTHROUGH_EMAIL ?? 'Test@example.com';
}

export function walkthroughPassword(): string {
  return process.env.WALKTHROUGH_PASSWORD ?? 'password';
}

/** Sign in via the login form, emitting typing and navigation timeline events. */
export async function signIn(
  page: Page,
  timeline: Timeline,
  options: SignInOptions = {},
): Promise<void> {
  const returnPath = options.returnPath ?? '/';
  const loginUrl =
    returnPath === '/'
      ? '/login'
      : `/login?returnUrl=${encodeURIComponent(returnPath)}`;

  await page.goto(loginUrl);
  await timeline.emit({
    kind: 'navigate',
    url: page.url(),
    phase: 'login',
  });

  await typeWithEvents(
    page,
    timeline,
    page.getByRole('textbox', { name: 'Email' }),
    walkthroughEmail(),
    { field: 'Email' },
  );

  await typeWithEvents(
    page,
    timeline,
    page.getByRole('textbox', { name: 'Password' }),
    walkthroughPassword(),
    { field: 'Password' },
  );

  const signInButton = page.getByRole('button', { name: 'Sign In' });
  await signInButton.hover();
  await timeline.emit({
    kind: 'ui.hover',
    target: 'Sign In',
    phase: 'login',
  });
  await signInButton.click();
  await timeline.emit({
    kind: 'ui.click',
    target: 'Sign In',
    phase: 'login',
  });

  await page.waitForURL((url) => !url.pathname.startsWith('/login'), {
    timeout: 30_000,
  });
  await timeline.emit({
    kind: 'navigate',
    url: page.url(),
    phase: 'post_login',
  });
}
