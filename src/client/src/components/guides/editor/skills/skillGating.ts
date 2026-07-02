import { isToolsetAvailable, TOOLSET_TO_TOOLS } from './skillToolsetMapping';

export interface SkillGatingInput {
  requiresToolsets: string[];
  requiresTools: string[];
  fallbackForToolsets: string[];
  fallbackForTools: string[];
}

export interface SkillGatingResult {
  satisfied: boolean;
  missingCapabilities: string[];
  suppressedByCapabilities: string[];
  summary: string;
  statusLabel: string;
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

  if (missing.size > 0) {
    const labels = [...missing].map((item) => item.replace(/^toolset:|^tool:/, ''));
    return {
      satisfied: false,
      missingCapabilities: labels,
      suppressedByCapabilities: [],
      summary: `Will not be offered to the model until ${labels.join(', ')} is added.`,
      statusLabel: 'Gated',
    };
  }

  const suppressed = new Set<string>();

  for (const toolset of skill.fallbackForToolsets) {
    if (isToolsetAvailable(toolset, availableToolTypes, hasCodeInterpreterFiles)) {
      suppressed.add(toolset);
    }
  }

  for (const tool of skill.fallbackForTools) {
    if (availableToolTypes.has(tool)) {
      suppressed.add(tool);
    }
  }

  if (suppressed.size > 0) {
    const labels = [...suppressed];
    return {
      satisfied: false,
      missingCapabilities: [],
      suppressedByCapabilities: labels,
      summary: `Will not be offered while ${labels.join(', ')} is available (primary capability replaces this fallback skill).`,
      statusLabel: 'Suppressed',
    };
  }

  if (skill.fallbackForToolsets.length > 0 || skill.fallbackForTools.length > 0) {
    const fallbackLabels = [
      ...skill.fallbackForToolsets,
      ...skill.fallbackForTools,
    ];
    return {
      satisfied: true,
      missingCapabilities: [],
      suppressedByCapabilities: [],
      summary: `Offered as a fallback when ${fallbackLabels.join(', ')} is unavailable.`,
      statusLabel: 'Prerequisites met',
    };
  }

  return {
    satisfied: true,
    missingCapabilities: [],
    suppressedByCapabilities: [],
    summary: 'All prerequisites satisfied by the current assistant tools.',
    statusLabel: 'Prerequisites met',
  };
}
