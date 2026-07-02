import { TOOLSET_TO_TOOLS } from './skillToolsetMapping';

export interface SkillGatingInput {
  requiresToolsets: string[];
  requiresTools: string[];
}

export interface SkillGatingResult {
  satisfied: boolean;
  missingCapabilities: string[];
  summary: string;
}

export function computeSkillGating(
  skill: SkillGatingInput,
  availableToolTypes: Set<string>,
  hasCodeInterpreterFiles: boolean,
): SkillGatingResult {
  const missing = new Set<string>();

  for (const toolset of skill.requiresToolsets) {
    const mapped = TOOLSET_TO_TOOLS[toolset.toLowerCase()];
    if (!mapped) {
      missing.add(`toolset:${toolset}`);
      continue;
    }

    const satisfied = mapped.some((toolType) => {
      if (toolType === 'code_interpreter') {
        return hasCodeInterpreterFiles || availableToolTypes.has('code_interpreter');
      }

      return availableToolTypes.has(toolType);
    });

    if (!satisfied) {
      missing.add(`toolset:${toolset}`);
    }
  }

  for (const tool of skill.requiresTools) {
    if (!availableToolTypes.has(tool)) {
      missing.add(`tool:${tool}`);
    }
  }

  if (missing.size === 0) {
    return {
      satisfied: true,
      missingCapabilities: [],
      summary: 'All prerequisites satisfied by the current assistant tools.',
    };
  }

  const labels = [...missing].map((item) => item.replace(/^toolset:|^tool:/, ''));
  return {
    satisfied: false,
    missingCapabilities: labels,
    summary: `Will not be offered to the model until ${labels.join(', ')} is added.`,
  };
}
