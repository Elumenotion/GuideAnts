import { describe, expect, it } from 'vitest';
import {
  isToolsetAvailable,
  mapSkillPrerequisites,
  TOOLSET_TO_TOOLS,
} from '../skillToolsetMapping';

describe('skillToolsetMapping', () => {
  it('maps known toolsets to catalog tool ids and code interpreter needs', () => {
    const result = mapSkillPrerequisites(['terminal', 'web'], ['WebSearch']);

    expect(result.toolIds).toEqual(
      expect.arrayContaining([
        'b0000000-0000-0000-0000-000000000008',
        'b0000000-0000-0000-0000-000000000009',
        'b0000000-0000-0000-0000-00000000000d',
      ]),
    );
    expect(result.needsCodeInterpreter).toBe(true);
    expect(result.mappings.some((entry) => entry.mappedCapability === '(unmapped)')).toBe(false);
  });

  it('records unmapped toolsets and tools', () => {
    const result = mapSkillPrerequisites(['unknown-toolset'], ['MissingTool']);

    expect(result.toolIds).toEqual([]);
    expect(result.mappings).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ requirement: 'requires_toolsets: unknown-toolset' }),
        expect.objectContaining({ requirement: 'requires_tools: MissingTool' }),
      ]),
    );
  });

  it('checks toolset availability against assistant capabilities', () => {
    expect(isToolsetAvailable('web', new Set(['WebSearch']), false)).toBe(true);
    expect(isToolsetAvailable('sandbox', new Set<string>(), true)).toBe(true);
    expect(isToolsetAvailable('terminal', new Set<string>(), false)).toBe(false);
    expect(isToolsetAvailable('missing', new Set<string>(), false)).toBe(false);
    expect(TOOLSET_TO_TOOLS.web).toContain('WebSearch');
  });
});
