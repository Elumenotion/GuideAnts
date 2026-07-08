import { describe, expect, it } from 'vitest';
import { computeSkillGating } from '../skillGating';

describe('skillGating', () => {
  it('reports missing toolsets when prerequisites are unavailable', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: ['terminal'],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set<string>(),
      false,
    );

    expect(result.satisfied).toBe(false);
    expect(result.statusLabel).toBe('Gated');
    expect(result.missingCapabilities).toContain('terminal');
  });

  it('reports suppressed fallback skills when primary capabilities exist', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: ['web'],
        fallbackForTools: [],
      },
      new Set(['WebSearch']),
      false,
    );

    expect(result.satisfied).toBe(false);
    expect(result.statusLabel).toBe('Suppressed');
    expect(result.suppressedByCapabilities).toContain('web');
  });

  it('describes fallback availability when prerequisites are otherwise met', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: ['web'],
        fallbackForTools: [],
      },
      new Set<string>(),
      false,
    );

    expect(result.satisfied).toBe(true);
    expect(result.summary).toMatch(/fallback when web is unavailable/i);
  });

  it('accepts code interpreter prerequisites from notebook payload files', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: ['sandbox'],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set<string>(),
      true,
    );

    expect(result.satisfied).toBe(true);
    expect(result.statusLabel).toBe('Prerequisites met');
  });

  it('flags unknown toolsets and missing required tools', () => {
    const unknownToolset = computeSkillGating(
      {
        requiresToolsets: ['unknown'],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set<string>(),
      false,
    );
    expect(unknownToolset.missingCapabilities).toContain('unknown');

    const missingTool = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: ['MissingTool'],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set<string>(),
      false,
    );
    expect(missingTool.missingCapabilities).toContain('MissingTool');
  });

  it('suppresses fallback skills when primary capabilities are already available', () => {
    const suppressed = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: ['web'],
        fallbackForTools: ['browser_navigate'],
      },
      new Set(['browser_navigate']),
      false,
    );

    expect(suppressed.satisfied).toBe(false);
    expect(suppressed.statusLabel).toBe('Suppressed');
    expect(suppressed.suppressedByCapabilities).toEqual(expect.arrayContaining(['browser_navigate']));
  });

  it('describes fallback skills when primary capabilities are unavailable', () => {
    const fallback = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: ['web'],
        fallbackForTools: ['browser_navigate'],
      },
      new Set(),
      false,
    );

    expect(fallback.satisfied).toBe(true);
    expect(fallback.statusLabel).toBe('Prerequisites met');
    expect(fallback.summary).toMatch(/fallback when web, browser_navigate is unavailable/i);
  });

  it('reports default prerequisites met when no gating rules apply', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set(['terminal']),
      false,
    );

    expect(result.satisfied).toBe(true);
    expect(result.summary).toBe('All prerequisites satisfied by the current assistant tools.');
  });
});
