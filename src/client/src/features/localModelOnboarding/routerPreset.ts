export interface PresetKeyValue {
  /** Stable React row identity — not persisted to router INI. */
  id?: string;
  key: string;
  value: string;
}

export function createPresetRow(key = '', value = ''): PresetKeyValue {
  return { id: crypto.randomUUID(), key, value };
}

export function presetRowKey(row: PresetKeyValue, index: number): string {
  return row.id ?? `preset-row-${index}`;
}

export function withStablePresetRowIds(rows: PresetKeyValue[]): PresetKeyValue[] {
  return rows.map((row, index) => (row.id ? row : { ...row, id: `preset-row-${index}-${crypto.randomUUID()}` }));
}

export function stripPresetRowMetadata(rows: PresetKeyValue[]): Array<{ key: string; value: string }> {
  return rows.map(({ key, value }) => ({ key, value }));
}

const INFRASTRUCTURE_KEYS = new Set(['model', 'mmproj', 'version']);

/** Edited via dedicated fields in AliasPresetSavePanel, not the free-form key list. */
const MANAGED_PRESET_KEYS = new Set(['ctx-size', 'cache-ram']);

export function isManagedPresetKey(key: string): boolean {
  return MANAGED_PRESET_KEYS.has(key.trim().toLowerCase());
}

export function filterManagedPresetRows(rows: PresetKeyValue[]): PresetKeyValue[] {
  return rows.filter((row) => row.key.trim() && !isManagedPresetKey(row.key));
}

export function splitManagedPresetFromRecord(record: Record<string, string>): {
  ctxSize: string;
  cacheRam: string;
  rows: PresetKeyValue[];
} {
  const rows = presetRowsFromRecord(record);
  const ctxSize = rows.find((row) => row.key.trim().toLowerCase() === 'ctx-size')?.value ?? '';
  const cacheRam = rows.find((row) => row.key.trim().toLowerCase() === 'cache-ram')?.value ?? '';
  return {
    ctxSize,
    cacheRam,
    rows: filterManagedPresetRows(rows),
  };
}

export function buildEffectivePresetRecord(
  rows: PresetKeyValue[],
  ctxSize: string,
  cacheRam: string,
): Record<string, string> {
  const preset = presetRecordFromRows(filterManagedPresetRows(rows));
  const trimmedCtxSize = ctxSize.trim();
  const trimmedCacheRam = cacheRam.trim();
  if (trimmedCtxSize) {
    preset['ctx-size'] = trimmedCtxSize;
  }
  if (trimmedCacheRam) {
    preset['cache-ram'] = trimmedCacheRam;
  }
  return preset;
}

/** Router shell keys belong on the process CLI, not in per-alias presets. */
const ROUTER_SHELL_KEYS = new Set([
  'models-preset',
  'models-max',
  'no-models-autoload',
  'no-autoload',
]);

const CONTROL_CHAR_REGEX = /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/;
const SHELL_FRAGMENT_REGEX = /[;&|`$<>]|\$\(|\$\{/;

export function presetRowsFromRecord(record: Record<string, string>): PresetKeyValue[] {
  return Object.entries(record).map(([key, value], index) => ({
    id: `preset-key-${index}-${key.trim().toLowerCase()}`,
    key,
    value,
  }));
}

export function presetRecordFromRows(rows: PresetKeyValue[]): Record<string, string> {
  const record: Record<string, string> = {};
  for (const row of rows) {
    const key = row.key.trim();
    if (!key) {
      continue;
    }
    record[key] = row.value.trim();
  }
  return record;
}

export function validateAliasPresetRows(rows: PresetKeyValue[]): string[] {
  const errors: string[] = [];
  const seenLower = new Map<string, string>();

  for (const row of rows) {
    const key = row.key.trim();
    const value = row.value;

    if (!key) {
      if (value.trim()) {
        errors.push('Preset keys cannot be blank when a value is provided.');
      }
      continue;
    }

    if (CONTROL_CHAR_REGEX.test(key)) {
      errors.push(`Preset key '${key}' contains control characters.`);
    }

    if (INFRASTRUCTURE_KEYS.has(key.toLowerCase())) {
      errors.push(`Preset cannot include infrastructure key '${key}'.`);
    }

    if (ROUTER_SHELL_KEYS.has(key.toLowerCase())) {
      errors.push(`Preset key '${key}' is router-shell infrastructure and cannot be set on a model alias.`);
    }

    const prior = seenLower.get(key.toLowerCase());
    if (prior && prior !== key) {
      errors.push(`Duplicate preset keys under case normalization: '${prior}' and '${key}'.`);
    }
    seenLower.set(key.toLowerCase(), key);

    if (value === null || value === undefined) {
      errors.push(`Preset value for '${key}' must be a string.`);
      continue;
    }

    const trimmedValue = value.trim();
    if (CONTROL_CHAR_REGEX.test(trimmedValue) || trimmedValue.includes('\n') || trimmedValue.includes('\r')) {
      errors.push(`Preset value for '${key}' contains control characters or newlines.`);
    }

    if (SHELL_FRAGMENT_REGEX.test(trimmedValue)) {
      errors.push(`Preset value for '${key}' contains shell metacharacters.`);
    }
  }

  return errors;
}

export function buildAliasIniPreview(alias: string, preset: Record<string, string>): string {
  const lines = [`[${alias.trim() || '<alias>'}]`];
  const sortedKeys = Object.keys(preset).sort((a, b) => a.localeCompare(b));
  for (const key of sortedKeys) {
    lines.push(`${key} = ${preset[key]}`);
  }
  return lines.join('\n');
}
