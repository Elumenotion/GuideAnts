import type { Locator, Page } from '@playwright/test';

import type { Timeline } from './timeline.js';

export interface TypeWithEventsOptions {
  delayMs?: number;
  field?: string;
}

/** Type text one character at a time, emitting per-char timeline events. */
export async function typeWithEvents(
  page: Page,
  timeline: Timeline,
  locator: Locator,
  text: string,
  options: TypeWithEventsOptions = {},
): Promise<void> {
  const delayMs = options.delayMs ?? 65;
  const field =
    options.field ??
    (await locator.getAttribute('aria-label')) ??
    (await locator.getAttribute('name')) ??
    'field';

  await locator.click();
  await timeline.emit({
    kind: 'typing.start',
    field,
    length: text.length,
  });

  for (let index = 0; index < text.length; index++) {
    const char = text[index]!;
    await page.keyboard.type(char, { delay: 0 });
    await timeline.emit({
      kind: 'typing.char',
      field,
      index,
      char,
    });
    if (delayMs > 0) {
      await page.waitForTimeout(delayMs);
    }
  }

  await timeline.emit({
    kind: 'typing.end',
    field,
    length: text.length,
  });
}
