import { describe, expect, it } from 'vitest';
import {
  buildAssistantInstructionsFromSkillMarkdown,
  filterSkillPayloadFilesForAssistant,
  isSkillManifestPath,
} from '../createFromSkillHelpers';

describe('createFromSkillHelpers', () => {
  it('detects skill manifest paths', () => {
    expect(isSkillManifestPath('skills/demo/SKILL.md')).toBe(true);
    expect(isSkillManifestPath('SKILL.md')).toBe(true);
    expect(isSkillManifestPath('skills/demo/readme.md')).toBe(false);
  });

  it('filters manifest files out of assistant payload uploads', () => {
    const files = [
      { folderKind: 'Skill', relativePath: 'skills/demo/SKILL.md', contentBytes: 'abc', contentType: 'text/markdown' },
      { folderKind: 'Skill', relativePath: 'skills/demo/helper.py', contentBytes: 'abc', contentType: 'text/plain' },
    ];

    expect(filterSkillPayloadFilesForAssistant(files)).toEqual([files[1]]);
  });

  it('trims markdown when building assistant instructions', () => {
    expect(buildAssistantInstructionsFromSkillMarkdown('  # Skill\n\nBody  ')).toBe('# Skill\n\nBody');
  });
});
