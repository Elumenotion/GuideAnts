import { expect, notebookPath, test } from '../../fixtures/walkthrough.js';
import { signIn, walkthroughPassword } from '../../lib/auth.js';
import { prepareChromeWindow } from '../../lib/window.js';

const INCIDENT_GUID = 'a71c69b4-69b5-4bb1-b80a-474e9e3b469d';
const ZERO_GUID = '00000000-0000-0000-0000-000000000000';

function projectIdFromNotebookPath(): string {
  const match = notebookPath().match(/^\/projects\/([^/]+)/);
  if (!match) {
    throw new Error(`WALKTHROUGH_NOTEBOOK_PATH missing project id: ${notebookPath()}`);
  }
  return match[1];
}

function guideEditorPath(): string | undefined {
  return process.env.WALKTHROUGH_GUIDE_PATH;
}

function guideApiId(): string | undefined {
  return process.env.WALKTHROUGH_GUIDE_ID;
}

function readerCredentials(): { email: string; password: string } | undefined {
  const email = process.env.WALKTHROUGH_READER_EMAIL;
  const password = process.env.WALKTHROUGH_READER_PASSWORD ?? walkthroughPassword();
  if (!email) {
    return undefined;
  }
  return { email, password };
}

async function expectNotFound(page: import('@playwright/test').Page): Promise<void> {
  const notFound = page.getByTestId('not-found-page');
  await expect(notFound).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('img', { name: 'GuideAnts' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'GuideAnts Notebooks' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Go to Home' })).toBeVisible();
}

async function expectNotNotFound(page: import('@playwright/test').Page): Promise<void> {
  await expect(page.getByTestId('not-found-page')).toHaveCount(0);
}

async function expectHomeShell(page: import('@playwright/test').Page): Promise<void> {
  await expect(page).toHaveURL((url) => url.pathname === '/' || url.pathname === '');
  await expectNotNotFound(page);
  await expect(page.getByRole('button', { name: 'Open Settings' })).toBeVisible({ timeout: 30_000 });
}

/**
 * Install fake timers, let them run at real speed through StartupGate/auth, then
 * pause once NotFound is visible so tests can fast-forward the redirect.
 */
async function gotoNotFoundWithClock(
  page: import('@playwright/test').Page,
  path: string,
): Promise<void> {
  await page.clock.install();
  await page.clock.resume();
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await expectNotFound(page);
  // pauseAt rejects targets in the past; pad slightly because resume() keeps moving.
  for (let attempt = 0; attempt < 5; attempt++) {
    const target = await page.evaluate(() => Date.now() + 50);
    try {
      await page.clock.pauseAt(target);
      return;
    } catch (error) {
      if (!String(error).includes('Cannot fast-forward to the past')) {
        throw error;
      }
    }
  }
  throw new Error('Could not pause Playwright clock after NotFound mounted');
}

async function redirectSecondsLeft(page: import('@playwright/test').Page): Promise<number> {
  const label = page.getByText(/Redirecting to home in \d+s/);
  await expect(label).toBeVisible();
  const text = await label.innerText();
  const match = text.match(/(\d+)/);
  if (!match) {
    throw new Error(`Could not parse redirect countdown: ${text}`);
  }
  return Number(match[1]);
}

test.describe('SPA unmatched-route 404', () => {
  test.describe('A. Defect A — unmatched routes show NotFound', () => {
    test('A1 signed in — /guides/{guid} shows NotFound', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto(`/guides/${INCIDENT_GUID}`, { waitUntil: 'domcontentloaded' });
      await expectNotFound(page);
      await expect(page).toHaveURL(new RegExp(`/guides/${INCIDENT_GUID}$`));
    });

    test('A2 signed in — unknown path shows NotFound', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto('/this-route-does-not-exist', { waitUntil: 'domcontentloaded' });
      await expectNotFound(page);
    });

    test('A3 signed in — /guides shows NotFound', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto('/guides', { waitUntil: 'domcontentloaded' });
      await expectNotFound(page);
    });

    test('A4 signed out — /guides/{guid} shows NotFound without login first', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto(`/guides/${INCIDENT_GUID}`, { waitUntil: 'domcontentloaded' });
      await expectNotFound(page);
      await expect(page).not.toHaveURL(/\/login/);
    });
  });

  test.describe('B. Auto-redirect (30 seconds)', () => {
    test('B1 still on NotFound just before redirect fires', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await gotoNotFoundWithClock(page, `/guides/${INCIDENT_GUID}`);
      const secondsLeft = await redirectSecondsLeft(page);
      // Stay one displayed second shy of fire (countdown uses Math.ceil).
      await page.clock.fastForward(Math.max(0, (secondsLeft - 1) * 1000));
      await expectNotFound(page);
      await expect(page).toHaveURL(new RegExp(`/guides/${INCIDENT_GUID}$`));
    });

    test('B2 redirects home after countdown elapses', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await gotoNotFoundWithClock(page, `/guides/${INCIDENT_GUID}`);
      const secondsLeft = await redirectSecondsLeft(page);
      await page.clock.fastForward(secondsLeft * 1000 + 500);
      await expectHomeShell(page);
    });

    test('B3 Go to Home cancels timer', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await gotoNotFoundWithClock(page, `/guides/${INCIDENT_GUID}`);
      await page.getByRole('button', { name: 'Go to Home' }).click();
      await expectHomeShell(page);
      await page.clock.fastForward(35_000);
      await expectHomeShell(page);
    });
  });

  test.describe('C. Matched public routes — must not become NotFound', () => {
    test('C1 /login', async ({ page, timeline, pointer: _pointer }) => {
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto('/login', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('button', { name: 'Sign In' })).toBeVisible({ timeout: 30_000 });
    });

    test('C2 /register', async ({ page, timeline, pointer: _pointer }) => {
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto('/register', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('heading', { name: 'Create Account' })).toBeVisible({
        timeout: 30_000,
      });
    });

    test('C3 /terms', async ({ page, timeline, pointer: _pointer }) => {
      await prepareChromeWindow(page, timeline);
      await page.goto('/terms', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('heading', { name: 'Terms of Service' })).toBeVisible({
        timeout: 30_000,
      });
    });

    test('C4 /privacy', async ({ page, timeline, pointer: _pointer }) => {
      await prepareChromeWindow(page, timeline);
      await page.goto('/privacy', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('heading', { name: 'Privacy Policy' })).toBeVisible({
        timeout: 30_000,
      });
    });

    test('C5 /public/{friendly} is PublicGuide, not NotFound', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      await prepareChromeWindow(page, timeline);
      await page.goto('/public/this-friendly-name-does-not-exist', {
        waitUntil: 'domcontentloaded',
      });
      await expectNotNotFound(page);
      await expect(
        page.getByRole('heading', { name: /Guide not found|Failed to load guide/ }),
      ).toBeVisible({ timeout: 30_000 });
    });
  });

  test.describe('D. Matched authenticated routes — smoke', () => {
    test('D1 / home', async ({ page, signedIn: _signedIn }) => {
      await page.goto('/', { waitUntil: 'domcontentloaded' });
      await expectHomeShell(page);
    });

    test('D2 /projects', async ({ page, signedIn: _signedIn }) => {
      await page.goto('/projects', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible({
        timeout: 30_000,
      });
    });

    test('D3 valid notebook path', async ({ page, signedIn: _signedIn }) => {
      await page.goto(notebookPath(), { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByTestId('notebook-service-toolbar')).toBeVisible({ timeout: 30_000 });
    });

    test('D4 guide editor path', async ({ page, signedIn: _signedIn }) => {
      const path = guideEditorPath();
      test.skip(!path, 'Set WALKTHROUGH_GUIDE_PATH for D4');
      await page.goto(path!, { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page).toHaveURL(new RegExp(path!.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    });

    test('D5 /settings', async ({ page, signedIn: _signedIn }) => {
      await page.goto('/settings', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('heading', { name: 'Settings' })).toBeVisible({
        timeout: 30_000,
      });
    });

    test('D6 /conversations', async ({ page, signedIn: _signedIn }) => {
      await page.goto('/conversations', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page.getByRole('heading', { name: 'All Conversations' })).toBeVisible({
        timeout: 30_000,
      });
    });
  });

  test.describe('E. Guards still redirect (not NotFound)', () => {
    test('E1 non-admin guides dashboard redirects away', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      const reader = readerCredentials();
      test.skip(!reader, 'Set WALKTHROUGH_READER_EMAIL for E1');
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto('/login', { waitUntil: 'domcontentloaded' });
      await page.getByRole('textbox', { name: 'Email' }).fill(reader!.email);
      await page.getByRole('textbox', { name: 'Password' }).fill(reader!.password);
      await page.getByRole('button', { name: 'Sign In' }).click();
      await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
      await page.goto(`/projects/${projectIdFromNotebookPath()}/guides`, {
        waitUntil: 'domcontentloaded',
      });
      await expectNotNotFound(page);
      await expect(page).not.toHaveURL(/\/guides$/);
    });

    test('E2 reader /new-project redirects home', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      const reader = readerCredentials();
      test.skip(!reader, 'Set WALKTHROUGH_READER_EMAIL for E2');
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto('/login', { waitUntil: 'domcontentloaded' });
      await page.getByRole('textbox', { name: 'Email' }).fill(reader!.email);
      await page.getByRole('textbox', { name: 'Password' }).fill(reader!.password);
      await page.getByRole('button', { name: 'Sign In' }).click();
      await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
      await page.goto('/new-project', { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expectHomeShell(page);
    });

    test('E3 signed out /projects/{id} goes to login with returnUrl', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      const projectPath = `/projects/${projectIdFromNotebookPath()}`;
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto(projectPath, { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
      await expect(page).toHaveURL(/\/login\?/);
      const url = new URL(page.url());
      expect(url.searchParams.get('returnUrl')).toContain(projectPath);
      await expect(page.getByRole('button', { name: 'Sign In' })).toBeVisible({ timeout: 30_000 });
    });
  });

  test.describe('F. Param match vs catch-all', () => {
    test('F1 nonsense project id still matches route', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto(`/projects/${ZERO_GUID}`, { waitUntil: 'domcontentloaded' });
      await expectNotNotFound(page);
    });

    test('F2 nonsense notebook id still matches route', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto(`/projects/${projectIdFromNotebookPath()}/notebooks/${ZERO_GUID}`, {
        waitUntil: 'domcontentloaded',
      });
      await expectNotNotFound(page);
    });
  });

  test.describe('G. Auth / OAuth / expiry paths untouched', () => {
    test('G1 /oauth/callback without code does not end on NotFound', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await page.goto('/oauth/callback', { waitUntil: 'domcontentloaded' });
      await expect
        .poll(async () => page.url(), { timeout: 30_000 })
        .not.toContain('/oauth/callback');
      await expectNotNotFound(page);
    });

    test('G2 auth-expired lands on login with returnUrl', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto('/', { waitUntil: 'domcontentloaded' });
      await expectHomeShell(page);
      await page.evaluate(() => {
        window.dispatchEvent(
          new CustomEvent('auth-expired', { detail: { reason: 'walkthrough' } }),
        );
      });
      await expect(page).toHaveURL(/\/login\?/, { timeout: 30_000 });
      await expectNotNotFound(page);
      const url = new URL(page.url());
      expect(url.searchParams.get('returnUrl')).toBeTruthy();
    });

    test('G3 login returnUrl to unmatched path lands on NotFound', async ({
      page,
      timeline,
      pointer: _pointer,
    }) => {
      await prepareChromeWindow(page, timeline);
      await page.context().clearCookies();
      await signIn(page, timeline, { returnPath: `/guides/${INCIDENT_GUID}` });
      await expectNotFound(page);
      await expect(page).toHaveURL(new RegExp(`/guides/${INCIDENT_GUID}$`));
    });
  });

  test.describe('H. Server + client contract', () => {
    test('H1 SPA shell serves unmatched path; client shows NotFound', async ({
      page,
      signedIn: _signedIn,
    }) => {
      const response = await page.goto(`/guides/${INCIDENT_GUID}`, {
        waitUntil: 'domcontentloaded',
      });
      expect(response?.status()).toBe(200);
      await expectNotFound(page);
    });

    test('H2 API guides endpoint still serves JSON', async ({
      page,
      request,
      signedIn: _signedIn,
    }) => {
      const id = guideApiId();
      test.skip(!id, 'Set WALKTHROUGH_GUIDE_ID for H2');
      // Ensure session cookie exists from signedIn; reuse browser cookies for API.
      const cookies = await page.context().cookies();
      const cookieHeader = cookies.map((c) => `${c.name}=${c.value}`).join('; ');
      const response = await request.get(`/api/guides/${id}`, {
        headers: cookieHeader ? { Cookie: cookieHeader } : undefined,
      });
      expect(response.status()).toBe(200);
      expect(response.headers()['content-type'] ?? '').toMatch(/json/i);
    });
  });

  test.describe('I. Style conformance', () => {
    test('I1–I2 NotFound branding and Go to Home', async ({
      page,
      signedIn: _signedIn,
    }) => {
      await page.goto('/foo/bar', { waitUntil: 'domcontentloaded' });
      await expectNotFound(page);
      await expect(page.locator('img[src="/code-ants.png"]')).toBeVisible();
      const home = page.getByRole('button', { name: 'Go to Home' });
      await expect(home).toBeVisible();
      await home.click();
      await expectHomeShell(page);
    });
  });
});
