import { describe, expect, it } from 'vitest';
import type { AssistantSkillDto } from '../../../../../types/guides';
import { moveSkill, nextSkillDisplayOrder, reindexSkillDisplayOrders, sortSkillsByDisplayOrder } from '../skillOrdering';

function makeSkill(name: string, displayOrder: number): AssistantSkillDto {
  return {
    name,
    description: name,
    enabled: true,
    displayOrder,
    source: 'Imported',
    requiresToolsets: [],
    requiresTools: [],
    fallbackForToolsets: [],
    fallbackForTools: [],
    files: [],
  };
}

describe('skillOrdering', () => {
  it('sorts skills by display order then name', () => {
    const sorted = sortSkillsByDisplayOrder([
      makeSkill('beta', 1),
      makeSkill('alpha', 0),
      makeSkill('gamma', 1),
    ]);

    expect(sorted.map((skill) => skill.name)).toEqual(['alpha', 'beta', 'gamma']);
  });

  it('reindexes display orders sequentially', () => {
    const reindexed = reindexSkillDisplayOrders([makeSkill('b', 5), makeSkill('a', 2)]);
    expect(reindexed.map((skill) => skill.displayOrder)).toEqual([0, 1]);
  });

  it('returns the next display order slot', () => {
    expect(nextSkillDisplayOrder([])).toBe(0);
    expect(nextSkillDisplayOrder([makeSkill('a', 2), makeSkill('b', 5)])).toBe(6);
  });

  it('moves skills up and down while reindexing', () => {
    const skills = [makeSkill('a', 0), makeSkill('b', 1), makeSkill('c', 2)];
    const movedDown = moveSkill(skills, 'a', 'down');
    expect(movedDown.map((skill) => skill.name)).toEqual(['b', 'a', 'c']);

    const movedUp = moveSkill(skills, 'c', 'up');
    expect(movedUp.map((skill) => skill.name)).toEqual(['a', 'c', 'b']);
    expect(moveSkill(skills, 'missing', 'up')).toBe(skills);
  });
});
