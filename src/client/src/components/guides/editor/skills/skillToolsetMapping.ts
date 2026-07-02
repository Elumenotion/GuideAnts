export const TOOLSET_TO_TOOLS: Record<string, string[]> = {
  sandbox: ['code_interpreter', 'run_python', 'run_bash'],
  terminal: ['code_interpreter', 'run_python', 'run_bash'],
  web: ['WebSearch', 'ReadWeb'],
};

export function isToolsetAvailable(
  toolset: string,
  availableToolTypes: Set<string>,
  hasCodeInterpreterFiles: boolean,
): boolean {
  const mapped = TOOLSET_TO_TOOLS[toolset.toLowerCase()];
  if (!mapped) {
    return false;
  }

  return mapped.some((toolType) => {
    if (toolType === 'code_interpreter') {
      return hasCodeInterpreterFiles || availableToolTypes.has('code_interpreter');
    }

    return availableToolTypes.has(toolType);
  });
}

export const CATALOG_TOOL_IDS: Record<string, string> = {
  WebSearch: 'b0000000-0000-0000-0000-00000000000d',
  ReadWeb: 'b0000000-0000-0000-0000-00000000000e',
  run_bash: 'b0000000-0000-0000-0000-000000000008',
  run_python: 'b0000000-0000-0000-0000-000000000009',
};

export const RUN_PYTHON_TOOL_ID = CATALOG_TOOL_IDS.run_python;

export interface SkillPrerequisiteMapping {
  requirement: string;
  mappedCapability: string;
  reason: string;
}

export interface CreateFromSkillMappingResult {
  toolIds: string[];
  needsCodeInterpreter: boolean;
  mappings: SkillPrerequisiteMapping[];
}

export function mapSkillPrerequisites(
  requiresToolsets: string[],
  requiresTools: string[],
): CreateFromSkillMappingResult {
  const toolIds = new Set<string>();
  const mappings: SkillPrerequisiteMapping[] = [];
  let needsCodeInterpreter = false;

  for (const toolset of requiresToolsets) {
    const mapped = TOOLSET_TO_TOOLS[toolset.toLowerCase()];
    if (!mapped) {
      mappings.push({
        requirement: `requires_toolsets: ${toolset}`,
        mappedCapability: '(unmapped)',
        reason: `Unknown toolset '${toolset}' has no explicit mapping.`,
      });
      continue;
    }

    for (const capability of mapped) {
      if (capability === 'code_interpreter') {
        needsCodeInterpreter = true;
        mappings.push({
          requirement: `requires_toolsets: ${toolset}`,
          mappedCapability: 'code_interpreter',
          reason: `Toolset '${toolset}' requires code interpreter capability.`,
        });
        continue;
      }

      const toolId = CATALOG_TOOL_IDS[capability];
      if (toolId) {
        toolIds.add(toolId);
      }

      mappings.push({
        requirement: `requires_toolsets: ${toolset}`,
        mappedCapability: capability,
        reason: `Toolset '${toolset}' maps to catalog tool '${capability}'.`,
      });
    }
  }

  for (const tool of requiresTools) {
    const toolId = CATALOG_TOOL_IDS[tool];
    if (toolId) {
      toolIds.add(toolId);
      mappings.push({
        requirement: `requires_tools: ${tool}`,
        mappedCapability: tool,
        reason: `Declared tool '${tool}' maps to catalog tool '${tool}'.`,
      });
      continue;
    }

    mappings.push({
      requirement: `requires_tools: ${tool}`,
      mappedCapability: '(unmapped)',
      reason: `Declared tool '${tool}' has no explicit catalog mapping.`,
    });
  }

  return {
    toolIds: [...toolIds],
    needsCodeInterpreter,
    mappings,
  };
}
