import type { AssistantSkillDto } from '../../../../types/guides';

export function sortSkillsByDisplayOrder(skills: AssistantSkillDto[]): AssistantSkillDto[] {
  return [...skills].sort((left, right) => {
    if (left.displayOrder !== right.displayOrder) {
      return left.displayOrder - right.displayOrder;
    }

    return left.name.localeCompare(right.name, undefined, { sensitivity: 'base' });
  });
}

export function reindexSkillDisplayOrders(skills: AssistantSkillDto[]): AssistantSkillDto[] {
  return sortSkillsByDisplayOrder(skills).map((skill, index) => ({
    ...skill,
    displayOrder: index,
  }));
}

export function nextSkillDisplayOrder(skills: AssistantSkillDto[]): number {
  if (skills.length === 0) {
    return 0;
  }

  return Math.max(...skills.map((skill) => skill.displayOrder)) + 1;
}

export function moveSkill(
  skills: AssistantSkillDto[],
  skillName: string,
  direction: 'up' | 'down',
): AssistantSkillDto[] {
  const sorted = sortSkillsByDisplayOrder(skills);
  const index = sorted.findIndex((skill) => skill.name === skillName);
  if (index < 0) {
    return skills;
  }

  const targetIndex = direction === 'up' ? index - 1 : index + 1;
  if (targetIndex < 0 || targetIndex >= sorted.length) {
    return skills;
  }

  const next = [...sorted];
  [next[index], next[targetIndex]] = [next[targetIndex], next[index]];
  return next.map((skill, order) => ({
    ...skill,
    displayOrder: order,
  }));
}
