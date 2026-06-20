import type { EnvironmentVariableDto } from '../../../../types/guides';
import { MASKED_SECRET_VALUE } from '../environmentVariableValidation';

const SECRET_REF_PATTERN = /^\{\{secret:([A-Za-z_][A-Za-z0-9_]*)\}\}$/;

export function formatSecretRef(variableName: string): string {
  return `{{secret:${variableName.trim()}}}`;
}

export function parseSecretRef(value: string | null | undefined): string | null {
  if (!value) return null;
  const match = SECRET_REF_PATTERN.exec(value.trim());
  return match?.[1] ?? null;
}

export function isSecretRef(value: string | null | undefined): boolean {
  return parseSecretRef(value) !== null;
}

export function listSecretVariables(variables: EnvironmentVariableDto[]): EnvironmentVariableDto[] {
  return variables.filter((variable) => variable.isSecret && variable.name.trim());
}

export function resolveSecretRef(
  variableName: string,
  variables: EnvironmentVariableDto[]
): string | null {
  const match = variables.find(
    (variable) => variable.name.trim().toUpperCase() === variableName.trim().toUpperCase()
  );
  if (!match?.value || match.value === MASKED_SECRET_VALUE) {
    return null;
  }
  return match.value;
}

export function resolveHeaderValues(
  headers: Record<string, string>,
  variables: EnvironmentVariableDto[]
): { resolved: Record<string, string>; missingRefs: string[] } {
  const resolved: Record<string, string> = {};
  const missingRefs: string[] = [];

  for (const [key, value] of Object.entries(headers)) {
    const refName = parseSecretRef(value);
    if (refName) {
      const secretValue = resolveSecretRef(refName, variables);
      if (secretValue === null) {
        missingRefs.push(refName);
        continue;
      }
      resolved[key] = secretValue;
      continue;
    }

    if (value) {
      resolved[key] = value;
    }
  }

  return { resolved, missingRefs };
}

export function normalizeHeaderValueForStorage(value: string): string {
  const trimmed = value.trim();
  const refName = parseSecretRef(trimmed);
  if (refName) {
    return formatSecretRef(refName);
  }
  return trimmed;
}
