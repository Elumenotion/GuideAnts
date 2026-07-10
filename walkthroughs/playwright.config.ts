import { defineConfig } from '@playwright/test';

const baseURL = process.env.WALKTHROUGH_BASE_URL ?? 'http://localhost:5107';
const windowPosition = process.env.WALKTHROUGH_WINDOW_POSITION;
const monitorWidth = Number(process.env.WALKTHROUGH_MONITOR_WIDTH ?? '2560');
const monitorHeight = Number(process.env.WALKTHROUGH_MONITOR_HEIGHT ?? '1440');

const chromeArgs = ['--silent-debugger-extension-api', '--start-maximized'];
if (windowPosition) {
  chromeArgs.push(`--window-position=${windowPosition}`);
}

export default defineConfig({
  testDir: './scenarios',
  timeout: 180_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL,
    channel: 'chrome',
    headless: false,
    viewport: { width: monitorWidth, height: monitorHeight },
    deviceScaleFactor: 1,
    trace: 'off',
    video: 'off',
    screenshot: 'off',
    launchOptions: {
      args: chromeArgs,
    },
  },
  projects: [{ name: 'chrome', use: { channel: 'chrome' } }],
});
