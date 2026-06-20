export const MASKED_SECRET_VALUE = '••••••••';
export const ENV_NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

const RESERVED_NAMES = new Set([
  'PATH',
  'HOME',
  'USER',
  'USERNAME',
  'SHELL',
  'LD_PRELOAD',
  'LD_LIBRARY_PATH',
  'DYLD_INSERT_LIBRARIES',
  'PYTHONPATH',
  'PYTHONHOME',
  'BASH_ENV',
  'ENV',
  'PROMPT_COMMAND',
  'PSMODULEPATH',
  'DOTNET_STARTUP_HOOKS',
  'DOTNET_ADDITIONAL_DEPS',
  'DOTNET_SHARED_STORE',
  'ASPNETCORE_ENVIRONMENT',
  'DOTNET_ENVIRONMENT',
  'SCRIPT_EXECUTION_AGENT_TOKEN',
  'SCRIPT_EXECUTION_ADMIN_TOKEN',
]);

export function findDuplicateEnvironmentNames(names: string[]): Set<string> {
  const seen = new Set<string>();
  const duplicates = new Set<string>();

  for (const name of names) {
    const normalized = name.trim().toUpperCase();
    if (!normalized) continue;
    if (seen.has(normalized)) {
      duplicates.add(normalized);
    }
    seen.add(normalized);
  }

  return duplicates;
}

export function validateEnvironmentVariableName(
  name: string,
  duplicateNames: Set<string>
): string | undefined {
  const trimmed = name.trim();
  if (!trimmed) return 'Name is required.';
  if (!ENV_NAME_PATTERN.test(trimmed)) {
    return 'Use letters, numbers, and underscores; start with a letter or underscore.';
  }
  if (
    RESERVED_NAMES.has(trimmed.toUpperCase()) ||
    trimmed.toUpperCase().startsWith('SCRIPT_EXECUTION_') ||
    trimmed.toUpperCase().startsWith('GUIDEANTS_')
  ) {
    return 'This name is reserved by script execution.';
  }
  if (duplicateNames.has(trimmed.toUpperCase())) {
    return 'Name must be unique within this section.';
  }
  return undefined;
}
