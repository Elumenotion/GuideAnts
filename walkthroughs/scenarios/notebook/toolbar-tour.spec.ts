import { expect, notebookPath, test, withDomWatch } from '../../fixtures/walkthrough.js';
import { ensureWalkthroughLayout } from '../../lib/layout.js';
import { pause, waitForNetworkSettled } from '../../lib/waits.js';

test.describe('notebook toolbar tour', () => {
  test('walk through service toolbar and header actions', async ({
    page,
    timeline,
    pointer,
    signedIn: _signedIn,
  }) => {
    await withDomWatch(page, timeline, async () => {
      await page.goto(notebookPath(), { waitUntil: 'domcontentloaded' });
      await ensureWalkthroughLayout(page);

      const toolbar = page.getByTestId('notebook-service-toolbar');
      const welcome = page.getByText('Welcome to your notebook');

      await expect(toolbar).toBeVisible({ timeout: 30_000 });
      await expect(welcome).toBeVisible({ timeout: 30_000 });
      await waitForNetworkSettled(page);

      await timeline.emit({
        kind: 'navigate',
        url: page.url(),
      });

      // Hold on the clean page before the intro overlay appears.
      await pause(timeline, {
        ms: 2_500,
        reason: 'page_settle_before_intro',
      });

      await pointer.ensureInstalled();

      const toolbarBox = await toolbar.boundingBox();
      if (!toolbarBox) {
        throw new Error('Notebook service toolbar not measurable');
      }

      await pointer.pointAtBox(toolbarBox, {
        title: 'Welcome!',
        subtitle: "Let's tour the notebook toolbar",
        showRing: false,
      });
      await pause(timeline, {
        ms: 4_000,
        reason: 'intro_narration',
      });
      await pause(timeline, {
        ms: 1_200,
        reason: 'intro_hold_after',
      });

      const serviceStops = [
        {
          locator: toolbar.getByRole('button', { name: 'Chat', exact: true }),
          title: 'Chat',
          subtitle: 'Open the AI chat panel for this notebook',
        },
        {
          locator: toolbar.getByRole('button', { name: 'Image generation' }),
          title: 'Image generation',
          subtitle: 'Generate images from prompts in the sandbox',
        },
        {
          locator: toolbar.getByRole('button', { name: 'Speech synthesis (TTS)' }),
          title: 'Speech synthesis',
          subtitle: 'Convert text to speech in the notebook',
        },
      ] as const;

      for (const stop of serviceStops) {
        await pointer.tourStop(stop.locator, {
          title: stop.title,
          subtitle: stop.subtitle,
          dwellMs: 1300,
        });
        await pause(timeline, { ms: 250, reason: 'between_stops' });
      }

      await pause(timeline, { ms: 600, reason: 'header_section_pause' });

      const headerStops = [
        {
          locator: page.getByRole('button', { name: 'GuideAnts Guide' }),
          title: 'GuideAnts Guide',
          subtitle: 'Launch the interactive product guide',
        },
        {
          locator: page.getByRole('button', { name: 'Open Settings' }),
          title: 'Settings',
          subtitle: 'Configure models, services, and preferences',
        },
        {
          locator: page.getByRole('button', { name: 'Start tour' }),
          title: 'Help tour',
          subtitle: 'Replay the built-in onboarding walkthrough',
        },
      ] as const;

      for (const stop of headerStops) {
        await pointer.tourStop(stop.locator, {
          title: stop.title,
          subtitle: stop.subtitle,
          dwellMs: 1300,
        });
        await pause(timeline, { ms: 250, reason: 'between_stops' });
      }

      const content = page.getByText('Welcome to your notebook');
      const contentBox = (await content.boundingBox()) ?? {
        x: toolbarBox.x + toolbarBox.width * 0.25,
        y: toolbarBox.y + toolbarBox.height + 120,
        width: toolbarBox.width,
        height: 280,
      };
      await pointer.pointAtBox(contentBox, {
        title: "You're all set!",
        subtitle: 'Toolbar tour complete',
        showRing: false,
        animate: true,
      });
      await pointer.flash();
      await pause(timeline, { ms: 1200, reason: 'outro_narration' });

      await timeline.emit({ kind: 'assert.pass', message: 'toolbar tour complete' });
    });
  });
});
