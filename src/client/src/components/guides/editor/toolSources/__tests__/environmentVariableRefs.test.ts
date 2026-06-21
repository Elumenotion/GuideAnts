import { describe, expect, it } from 'vitest';
import {
  formatSecretRef,
  parseSecretRef,
  resolveHeaderValues,
} from '../environmentVariableRefs';

describe('environmentVariableRefs', () => {
  it('formats and parses secret refs', () => {
    expect(formatSecretRef('MCP_API_KEY')).toBe('{{secret:MCP_API_KEY}}');
    expect(parseSecretRef('{{secret:MCP_API_KEY}}')).toBe('MCP_API_KEY');
    expect(parseSecretRef('Bearer abc')).toBeNull();
  });

  it('resolves header secret refs from guide environment', () => {
    const { resolved, missingRefs } = resolveHeaderValues(
      { Authorization: '{{secret:MCP_API_KEY}}' },
      [{ name: 'MCP_API_KEY', value: 'abc123', isSecret: true }]
    );

    expect(resolved).toEqual({ Authorization: 'abc123' });
    expect(missingRefs).toEqual([]);
  });

  it('reports missing secret refs', () => {
    const { resolved, missingRefs } = resolveHeaderValues(
      { Authorization: '{{secret:MISSING}}' },
      []
    );

    expect(resolved).toEqual({});
    expect(missingRefs).toEqual(['MISSING']);
  });
});
