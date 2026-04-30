import type { ProviderEditorStateDto } from '../../../types/settings';
import { getProviderFieldLabel } from '../constants/displayLabels';

function isProbablyUrl(value: string): boolean {
  try {
    const u = new URL(value);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Validates visible operative provider fields for save. Returns per-field error messages.
 */
export function validateOperativeProviderFields(
  provider: ProviderEditorStateDto,
  draft: Record<string, unknown>
): Record<string, string> {
  const errors: Record<string, string> = {};
  const operative = provider.fieldMetadata.filter((m) => provider.operativeFields.includes(m.name));

  for (const meta of operative) {
    const label = getProviderFieldLabel(provider.providerId, meta.name);
    const fieldDto = provider.fields[meta.name];
    const raw =
      draft[meta.name] !== undefined && draft[meta.name] !== null
        ? String(draft[meta.name]).trim()
        : (fieldDto?.value ?? '').trim();

    if (meta.kind === 'secret') {
      const hasStored = fieldDto?.hasValue === true && fieldDto?.isSecret === true;
      if (meta.required && !hasStored && raw.length === 0) {
        errors[meta.name] = `${label} is required.`;
      }
      continue;
    }

    if (meta.required && raw.length === 0) {
      errors[meta.name] = `${label} is required.`;
      continue;
    }

    if (raw.length === 0) {
      continue;
    }

    switch (meta.kind) {
      case 'url':
        if (!isProbablyUrl(raw)) {
          errors[meta.name] = `${label} must be a valid http(s) URL.`;
        }
        break;
      case 'int': {
        const n = Number.parseInt(raw, 10);
        if (!Number.isFinite(n)) {
          errors[meta.name] = `${label} must be a whole number.`;
          break;
        }
        if (meta.name === 'TimeoutSeconds' && n <= 0) {
          errors[meta.name] = `${label} must be greater than zero.`;
        }
        if (meta.name === 'LocalMinIntervalMs' && n < 0) {
          errors[meta.name] = `${label} must be zero or greater.`;
        }
        if (
          (meta.name === 'MaxConcurrentConversions' ||
            meta.name === 'MaxRetries' ||
            meta.name === 'AsyncStatusPollIntervalMs' ||
            meta.name === 'Dimensions') &&
          n <= 0
        ) {
          errors[meta.name] = `${label} must be greater than zero.`;
        }
        break;
      }
      case 'enum':
        if (meta.enumOptions && meta.enumOptions.length > 0 && !meta.enumOptions.includes(raw)) {
          errors[meta.name] = `${label} must be one of: ${meta.enumOptions.join(', ')}.`;
        }
        break;
      default:
        if (meta.name === 'ApiVersion' && raw.length > 0) {
          if (!/^\d{4}-\d{2}-\d{2}(-[a-zA-Z0-9.-]+)?$/.test(raw)) {
            errors[meta.name] = `${label} should look like a date-based API version (e.g. 2024-11-30 or 2025-04-01-preview).`;
          }
        }
        break;
    }
  }

  return errors;
}

export function hasValidationErrors(errors: Record<string, string>): boolean {
  return Object.keys(errors).length > 0;
}

export function buildSavePayload(
  provider: ProviderEditorStateDto,
  draft: Record<string, unknown>
): Record<string, string | null> {
  const payload: Record<string, string | null> = {};
  const operative = provider.fieldMetadata.filter((m) => provider.operativeFields.includes(m.name));

  for (const meta of operative) {
    const fieldDto = provider.fields[meta.name];
    const fromDraft = draft[meta.name];
    const raw =
      fromDraft !== undefined && fromDraft !== null ? String(fromDraft).trim() : undefined;

    if (meta.kind === 'secret') {
      const hasStored = fieldDto?.hasValue === true && fieldDto?.isSecret === true;
      if (raw !== undefined && raw.length > 0) {
        payload[meta.name] = raw;
      } else if (!hasStored && meta.required) {
        payload[meta.name] = '';
      } else if (hasStored && (raw === undefined || raw.length === 0)) {
        // omit — server keeps existing secret
        continue;
      } else {
        payload[meta.name] = raw ?? null;
      }
      continue;
    }

    if (raw !== undefined) {
      payload[meta.name] = raw.length > 0 ? raw : null;
    }
  }

  return payload;
}
