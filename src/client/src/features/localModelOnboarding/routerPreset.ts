export interface PresetKeyValue {
  key: string;
  value: string;
}

const INFRASTRUCTURE_KEYS = new Set(['model', 'mmproj', 'version']);

const CONTROL_CHAR_REGEX = /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/;
const SHELL_FRAGMENT_REGEX = /[;&|`$<>]|\$\(|\$\{/;

export function presetRowsFromRecord(record: Record<string, string>): PresetKeyValue[] {
  return Object.entries(record).map(([key, value]) => ({ key, value }));
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
