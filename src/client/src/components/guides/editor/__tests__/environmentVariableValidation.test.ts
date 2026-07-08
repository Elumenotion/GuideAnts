import { describe, expect, it } from 'vitest';
import {
  ENV_NAME_PATTERN,
  MASKED_SECRET_VALUE,
  findDuplicateEnvironmentNames,
  validateEnvironmentVariableName,
} from '../environmentVariableValidation';

describe('environmentVariableValidation', () => {
  it('exposes stable constants', () => {
    expect(MASKED_SECRET_VALUE).toBe('••••••••');
    expect(ENV_NAME_PATTERN.test('MY_VAR_1')).toBe(true);
    expect(ENV_NAME_PATTERN.test('1BAD')).toBe(false);
  });

  it('finds duplicate names case-insensitively', () => {
    const duplicates = findDuplicateEnvironmentNames(['FOO', 'bar', ' foo ', 'BAZ']);
    expect(duplicates).toEqual(new Set(['FOO']));
  });

  it('ignores blank names when finding duplicates', () => {
    expect(findDuplicateEnvironmentNames(['', '  ', 'OK'])).toEqual(new Set());
  });

  it('rejects empty names', () => {
    expect(validateEnvironmentVariableName('   ', new Set())).toBe('Name is required.');
  });

  it('rejects invalid identifier shapes', () => {
    expect(validateEnvironmentVariableName('bad-name', new Set())).toBe(
      'Use letters, numbers, and underscores; start with a letter or underscore.',
    );
  });

  it('rejects reserved and prefixed names', () => {
    expect(validateEnvironmentVariableName('PATH', new Set())).toBe(
      'This name is reserved by script execution.',
    );
    expect(validateEnvironmentVariableName('SCRIPT_EXECUTION_TOKEN', new Set())).toBe(
      'This name is reserved by script execution.',
    );
    expect(validateEnvironmentVariableName('GUIDEANTS_SECRET', new Set())).toBe(
      'This name is reserved by script execution.',
    );
  });

  it('rejects duplicate names within a section', () => {
    expect(validateEnvironmentVariableName('MY_VAR', new Set(['MY_VAR']))).toBe(
      'Name must be unique within this section.',
    );
  });

  it('accepts valid unique names', () => {
    expect(validateEnvironmentVariableName('CUSTOM_API_KEY', new Set())).toBeUndefined();
  });
});
