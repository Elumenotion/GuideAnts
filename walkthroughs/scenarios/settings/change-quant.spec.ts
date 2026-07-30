import { expect, test, type Locator, type Page } from '@playwright/test';

import { signIn } from '../../lib/auth.js';
import { Timeline } from '../../lib/timeline.js';

const EDIT_DIALOG = /^Edit Catalog Row:/;
const CHANGE_QUANT_DIALOG = /^Change quant:/;

async function openModelsCatalog(page: Page): Promise<void> {
  await page.goto('/settings');
  await page
    .getByRole('navigation', { name: 'Settings tabs' })
    .getByRole('button', { name: 'Models & Runtime' })
    .click();
  await expect(page.getByRole('columnheader', { name: 'Model id' })).toBeVisible({ timeout: 30_000 });
}

/** First catalog row whose edit dialog exposes the llama-cpp installed summary. */
async function openLlamaEditDialog(page: Page): Promise<Locator> {
  const rows = page.getByRole('row').filter({ has: page.getByText('llama-cpp') });
  const count = await rows.count();
  expect(count, 'expected at least one llama-cpp catalog row in this environment').toBeGreaterThan(0);

  await rows.first().getByRole('button', { name: /^Edit model / }).click();
  const dialog = page.getByRole('dialog', { name: EDIT_DIALOG });
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  return dialog;
}

test.describe('change quant UI', () => {
  test('installed quant is stated and the dialog offers a quant dropdown', async ({ page }, testInfo) => {
    const timeline = new Timeline(testInfo);
    await timeline.startScenario({ mode: 'verify' });
    await signIn(page, timeline, { returnPath: '/settings' });

    await openModelsCatalog(page);
    const editDialog = await openLlamaEditDialog(page);

    // The installed quant must be readable without expanding anything.
    await expect(editDialog.getByText(/Installed quant:/)).toBeVisible({ timeout: 30_000 });

    const changeQuant = editDialog.getByRole('button', { name: 'Change quant' });
    await expect(changeQuant).toBeVisible();
    await changeQuant.click();

    const quantDialog = page.getByRole('dialog', { name: CHANGE_QUANT_DIALOG });
    await expect(quantDialog).toBeVisible();

    // Quant groups resolve from the repository.
    await expect(quantDialog.getByText('Loading quant groups…')).toBeHidden({ timeout: 60_000 });
    await expect(quantDialog.getByRole('alert')).toHaveCount(0);

    // Current quant is named in the body, not only inside the option list.
    await expect(quantDialog.getByText('Installed quant')).toBeVisible();

    const select = quantDialog.getByLabel('New quant group');
    await expect(select).toBeVisible();
    const optionCount = await select.locator('option').count();
    expect(optionCount, 'expected quant options beyond the placeholder').toBeGreaterThan(1);

    // Footer actions stay reachable regardless of how many quants the repo has.
    const start = quantDialog.getByRole('button', { name: 'Start change quant' });
    await expect(start).toBeVisible();
    await expect(start).toBeDisabled();

    // Selecting a quant enables submission and describes the replacement.
    const firstEnabled = select.locator('option:not([disabled])').nth(1);
    const quantValue = await firstEnabled.getAttribute('value');
    expect(quantValue).toBeTruthy();
    await select.selectOption(quantValue!);
    await expect(start).toBeEnabled();

    await timeline.endScenario('pass', {});
    timeline.writeManifest();
  });

  test('Esc closes only the change-quant dialog', async ({ page }, testInfo) => {
    const timeline = new Timeline(testInfo);
    await timeline.startScenario({ mode: 'verify' });
    await signIn(page, timeline, { returnPath: '/settings' });

    await openModelsCatalog(page);
    const editDialog = await openLlamaEditDialog(page);
    await editDialog.getByRole('button', { name: 'Change quant' }).click();

    const quantDialog = page.getByRole('dialog', { name: CHANGE_QUANT_DIALOG });
    await expect(quantDialog).toBeVisible();

    await page.keyboard.press('Escape');

    await expect(quantDialog).toBeHidden();
    await expect(editDialog).toBeVisible();

    await timeline.endScenario('pass', {});
    timeline.writeManifest();
  });
});
