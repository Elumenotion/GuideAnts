export const SKILL_DESCRIPTION_RECOMMENDED_LENGTH = 60;
export const SKILL_DESCRIPTION_MAX_LENGTH = 1024;

export const SKILL_DESCRIPTION_LIMITS_HINT =
  `Recommended description length is ${SKILL_DESCRIPTION_RECOMMENDED_LENGTH} characters per the skill spec. ` +
  `Hard limit in the database is ${SKILL_DESCRIPTION_MAX_LENGTH} characters.`;

export interface SkillDescriptionNormalization {
  description: string;
  truncated: boolean;
  exceedsRecommended: boolean;
}

export function normalizeSkillDescription(description: string): SkillDescriptionNormalization {
  const trimmed = description.trim();
  const truncated = trimmed.length > SKILL_DESCRIPTION_MAX_LENGTH;
  const normalized = truncated
    ? trimmed.slice(0, SKILL_DESCRIPTION_MAX_LENGTH)
    : trimmed;

  return {
    description: normalized,
    truncated,
    exceedsRecommended: normalized.length > SKILL_DESCRIPTION_RECOMMENDED_LENGTH,
  };
}

export function getSkillDescriptionWarning(length: number): string | null {
  if (length > SKILL_DESCRIPTION_MAX_LENGTH) {
    return `Description exceeds the ${SKILL_DESCRIPTION_MAX_LENGTH}-character database limit and will be truncated on save.`;
  }

  if (length > SKILL_DESCRIPTION_RECOMMENDED_LENGTH) {
    return SKILL_DESCRIPTION_LIMITS_HINT;
  }

  return null;
}

export function buildSkillDescriptionImportWarnings(
  normalized: SkillDescriptionNormalization,
): string[] {
  if (normalized.truncated) {
    return [
      `Skill description was truncated to ${SKILL_DESCRIPTION_MAX_LENGTH} characters to fit the database limit.`,
      SKILL_DESCRIPTION_LIMITS_HINT,
    ];
  }

  if (normalized.exceedsRecommended) {
    return [SKILL_DESCRIPTION_LIMITS_HINT];
  }

  return [];
}
