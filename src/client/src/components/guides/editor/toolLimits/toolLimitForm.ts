export type ParseOptionalPositiveIntResult =
  | { ok: true; value: number | undefined }
  | { ok: false; error: string };

export function parseOptionalPositiveInt(raw: string): ParseOptionalPositiveIntResult {
  if (raw.trim() === '') {
    return { ok: true, value: undefined };
  }

  const parsed = Number.parseInt(raw, 10);
  if (Number.isNaN(parsed)) {
    return { ok: false, error: 'Enter a whole number or leave blank for no limit.' };
  }

  if (parsed < 1) {
    return { ok: false, error: 'Limit must be at least 1 or left blank for no limit.' };
  }

  return { ok: true, value: parsed };
}
