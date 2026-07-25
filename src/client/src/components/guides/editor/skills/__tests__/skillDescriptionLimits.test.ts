import { describe, expect, it } from 'vitest';
import {
  buildSkillDescriptionImportWarnings,
  getSkillDescriptionWarning,
  normalizeSkillDescription,
  SKILL_DESCRIPTION_LIMITS_HINT,
  SKILL_DESCRIPTION_MAX_LENGTH,
  SKILL_DESCRIPTION_RECOMMENDED_LENGTH,
} from '../skillDescriptionLimits';

describe('skillDescriptionLimits', () => {
  it('normalizes whitespace and truncates to the database limit', () => {
    const long = 'a'.repeat(SKILL_DESCRIPTION_MAX_LENGTH + 25);
    const result = normalizeSkillDescription(`  ${long}  `);

    expect(result.description).toHaveLength(SKILL_DESCRIPTION_MAX_LENGTH);
    expect(result.truncated).toBe(true);
    expect(result.exceedsRecommended).toBe(true);
  });

  it('flags descriptions longer than the recommended length', () => {
    const result = normalizeSkillDescription('x'.repeat(SKILL_DESCRIPTION_RECOMMENDED_LENGTH + 1));

    expect(result.truncated).toBe(false);
    expect(result.exceedsRecommended).toBe(true);
  });

  it('returns guidance when the description exceeds the recommended length', () => {
    expect(getSkillDescriptionWarning(SKILL_DESCRIPTION_RECOMMENDED_LENGTH)).toBeNull();
    expect(getSkillDescriptionWarning(SKILL_DESCRIPTION_RECOMMENDED_LENGTH + 1)).toBe(
      SKILL_DESCRIPTION_LIMITS_HINT,
    );
  });

  it('builds import warnings for truncation and long descriptions', () => {
    expect(buildSkillDescriptionImportWarnings({
      description: 'short',
      truncated: false,
      exceedsRecommended: false,
    })).toEqual([]);

    const truncated = normalizeSkillDescription('x'.repeat(SKILL_DESCRIPTION_MAX_LENGTH + 10));
    expect(buildSkillDescriptionImportWarnings(truncated)).toEqual([
      `Skill description was truncated to ${SKILL_DESCRIPTION_MAX_LENGTH} characters to fit the database limit.`,
      SKILL_DESCRIPTION_LIMITS_HINT,
    ]);
  });
});
