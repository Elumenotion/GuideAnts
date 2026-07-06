import { describe, it, expect } from 'vitest';
import { parseSkillFrontmatter, buildCanonicalSkillMarkdown } from '../skillFrontmatter';
import { computeSkillGating } from '../skillGating';
import { mapSkillPrerequisites } from '../skillToolsetMapping';
import { buildSkillCardViewModel } from '../skillCardViewModel';
import { moveSkill, nextSkillDisplayOrder, reindexSkillDisplayOrders } from '../skillOrdering';
import { buildSkillFileTree, skillPackagePath } from '../skillFileTreeModel';
import {
  buildAssistantInstructionsFromSkillMarkdown,
  filterSkillPayloadFilesForAssistant,
  isSkillManifestPath,
} from '../createFromSkillHelpers';
import type { AssistantSkillDto } from '../../../../../types/guides';

const agentskillsYaml = `---
name: pptx-author
description: Build export-ready PowerPoint decks.
metadata:
  guideants:
    enabled: true
    display_order: 10
    requires_toolsets: [sandbox]
    requires_tools: [WebSearch]
---
# Body
`;

const hermesYaml = `---
name: hermes-skill
description: Hermes dialect skill.
metadata:
  hermes:
    enabled: true
    display_order: 5
    requires_toolsets: [web]
---
Body text.
`;

const claudeYaml = `---
name: claude-skill
description: Claude Code dialect skill.
allowed-tools:
  - Bash
argument-hint: "[topic]"
---
Slash command body.
`;

describe('skillFrontmatter', () => {
  it('parses agentskills dialect', () => {
    const parsed = parseSkillFrontmatter(agentskillsYaml);
    expect(parsed.frontmatter.name).toBe('pptx-author');
    expect(parsed.frontmatter.requiresToolsets).toEqual(['sandbox']);
    expect(parsed.frontmatter.requiresTools).toEqual(['WebSearch']);
  });

  it('parses hermes dialect', () => {
    const parsed = parseSkillFrontmatter(hermesYaml);
    expect(parsed.frontmatter.name).toBe('hermes-skill');
    expect(parsed.frontmatter.requiresToolsets).toEqual(['web']);
  });

  it('parses claude-code dialect', () => {
    const parsed = parseSkillFrontmatter(claudeYaml);
    expect(parsed.frontmatter.requiresTools).toEqual(['Bash']);
  });

  it('throws when name is missing', () => {
    expect(() => parseSkillFrontmatter(`---
description: missing name
---
body
`)).toThrow(/name/);
  });
});

describe('skillGating', () => {
  it('reports satisfied when required tools are present', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: ['web'],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set(['WebSearch', 'ReadWeb']),
      false,
    );
    expect(result.satisfied).toBe(true);
    expect(result.statusLabel).toBe('Prerequisites met');
  });

  it('reports unsatisfied honestly', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: ['web'],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set([]),
      false,
    );
    expect(result.satisfied).toBe(false);
    expect(result.statusLabel).toBe('Gated');
    expect(result.summary).toContain('Will not be offered');
  });

  it('suppresses fallback skills when the primary toolset is available', () => {
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
    expect(result.summary).toContain('web');
  });

  it('offers fallback skills when the primary toolset is unavailable', () => {
    const result = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: ['web'],
        fallbackForTools: [],
      },
      new Set([]),
      false,
    );
    expect(result.satisfied).toBe(true);
    expect(result.statusLabel).toBe('Prerequisites met');
    expect(result.summary).toContain('fallback');
  });
});

describe('skillToolsetMapping', () => {
  it('maps web toolset to catalog tool ids', () => {
    const result = mapSkillPrerequisites(['web'], []);
    expect(result.toolIds).toHaveLength(2);
    expect(result.mappings.some((item) => item.mappedCapability === 'WebSearch')).toBe(true);
  });

  it('does not guess unknown toolsets', () => {
    const result = mapSkillPrerequisites(['unknown'], []);
    expect(result.toolIds).toHaveLength(0);
    expect(result.mappings[0].mappedCapability).toBe('(unmapped)');
  });
});

describe('skillCardViewModel', () => {
  it('builds source and gating badges', () => {
    const gating = computeSkillGating(
      {
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      new Set(['WebSearch']),
      false,
    );
    const vm = buildSkillCardViewModel('Imported', gating, [
      { relativePath: 'Skills/demo/references/guide.md' },
    ]);
    expect(vm.sourceClassName).toContain('bg-gray-100');
    expect(vm.payloadFileCount).toBe(1);
  });
});

describe('buildCanonicalSkillMarkdown', () => {
  it('writes canonical authored skill markdown', () => {
    const markdown = buildCanonicalSkillMarkdown({
      name: 'demo',
      description: 'Demo skill',
      enabled: true,
      displayOrder: 1,
      body: '# Hello',
      source: 'Authored',
    });
    expect(markdown).toContain('name: demo');
    expect(markdown).toContain('source: Authored');
    expect(markdown).toContain('# Hello');
  });
});

describe('skillOrdering', () => {
  const sampleSkills = (): AssistantSkillDto[] => [
    {
      name: 'alpha',
      description: 'A',
      enabled: true,
      displayOrder: 0,
      source: 'Imported',
      files: [],
      requiresToolsets: [],
      requiresTools: [],
      fallbackForToolsets: [],
      fallbackForTools: [],
    },
    {
      name: 'beta',
      description: 'B',
      enabled: true,
      displayOrder: 0,
      source: 'Imported',
      files: [],
      requiresToolsets: [],
      requiresTools: [],
      fallbackForToolsets: [],
      fallbackForTools: [],
    },
  ];

  it('assigns the next display order for imports', () => {
    expect(nextSkillDisplayOrder(sampleSkills())).toBe(1);
  });

  it('reindexes skills after reordering', () => {
    const moved = moveSkill(sampleSkills(), 'beta', 'up');
    expect(moved.map((skill) => skill.name)).toEqual(['beta', 'alpha']);
    expect(reindexSkillDisplayOrders(moved).map((skill) => skill.displayOrder)).toEqual([0, 1]);
  });

  it('reindexes duplicate display orders to unique sequence', () => {
    const skills: AssistantSkillDto[] = [
      {
        name: 'arxiv',
        description: 'A',
        enabled: true,
        displayOrder: 0,
        source: 'Imported',
        files: [],
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      {
        name: 'qa-authored-a8',
        description: 'B',
        enabled: true,
        displayOrder: 0,
        source: 'Authored',
        files: [],
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
      {
        name: 'kanban-video-orchestrator',
        description: 'C',
        enabled: true,
        displayOrder: 1,
        source: 'Imported',
        files: [],
        requiresToolsets: [],
        requiresTools: [],
        fallbackForToolsets: [],
        fallbackForTools: [],
      },
    ];

    expect(reindexSkillDisplayOrders(skills).map((skill) => skill.displayOrder)).toEqual([0, 1, 2]);
  });
});

describe('createFromSkillHelpers', () => {
  it('uses the full SKILL.md file for assistant instructions', () => {
    const instructions = buildAssistantInstructionsFromSkillMarkdown(agentskillsYaml);
    expect(instructions).toContain('name: pptx-author');
    expect(instructions).toContain('# Body');
    expect(instructions).not.toContain('Use the');
  });

  it('excludes SKILL.md from assistant payload files', () => {
    expect(isSkillManifestPath('Skills/demo/SKILL.md')).toBe(true);
    expect(isSkillManifestPath('Skills/demo/scripts/run.sh')).toBe(false);

    const filtered = filterSkillPayloadFilesForAssistant([
      {
        folderKind: 'Skill',
        relativePath: 'Skills/searxng-search/SKILL.md',
        contentBytes: btoa('# skill'),
        contentType: 'text/markdown',
      },
      {
        folderKind: 'Skill',
        relativePath: 'Skills/searxng-search/scripts/searxng.sh',
        contentBytes: btoa('#!/bin/bash'),
        contentType: 'application/x-sh',
      },
    ]);

    expect(filtered).toHaveLength(1);
    expect(filtered[0].relativePath).toBe('Skills/searxng-search/scripts/searxng.sh');
  });
});

describe('skillFileTree', () => {
  it('strips the skill folder prefix', () => {
    expect(skillPackagePath('Skills/arxiv/scripts/search.py', 'arxiv')).toBe('scripts/search.py');
  });

  it('builds a folder tree for package files', () => {
    const tree = buildSkillFileTree(
      [
        {
          id: '1',
          folderKind: 'Skill',
          relativePath: 'Skills/demo/SKILL.md',
          created: 'now',
        },
        {
          id: '2',
          folderKind: 'Skill',
          relativePath: 'Skills/demo/scripts/run.py',
          created: 'now',
        },
      ],
      'demo',
    );

    expect(tree.some((node) => node.name === 'SKILL.md')).toBe(true);
    expect(tree.some((node) => node.isFolder && node.name === 'scripts')).toBe(true);
  });
});
