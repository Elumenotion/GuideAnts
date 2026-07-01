import { describe, it, expect } from 'vitest';
import {
  guideHasSandboxGatingPayload,
  guideHasSkillScriptsPayload,
  isMaterializableSkillPayloadPath,
} from '../executablePayload';

describe('isMaterializableSkillPayloadPath', () => {
  it('accepts skill scripts and assets', () => {
    expect(isMaterializableSkillPayloadPath('Skills/kanban/scripts/monitor.py')).toBe(true);
    expect(isMaterializableSkillPayloadPath('Skills/kanban/assets/template.md.tmpl')).toBe(true);
  });

  it('rejects manifests and references', () => {
    expect(isMaterializableSkillPayloadPath('Skills/kanban/SKILL.md')).toBe(false);
    expect(isMaterializableSkillPayloadPath('Skills/kanban/references/guide.md')).toBe(false);
  });
});

describe('guideHasSkillScriptsPayload', () => {
  it('returns false for code interpreter files alone', () => {
    expect(guideHasSkillScriptsPayload({
      existingFiles: [{ id: '1', folderKind: 'CodeInterpreter', relativePath: 'run.py', created: '' }],
      newFiles: [],
      skills: [],
      pendingSkillUploads: [],
    })).toBe(false);
  });

  it('returns true for skill scripts on the guide', () => {
    expect(guideHasSkillScriptsPayload({
      existingFiles: [],
      newFiles: [],
      skills: [{
        name: 'kanban',
        description: 'd',
        enabled: true,
        displayOrder: 0,
        source: 'Imported',
        requiresToolsets: [],
        requiresTools: [],
        files: [{ id: '1', folderKind: 'Skill', relativePath: 'Skills/kanban/scripts/monitor.py', created: '' }],
      }],
      pendingSkillUploads: [],
    })).toBe(true);
  });
});

describe('guideHasSandboxGatingPayload', () => {
  it('returns true for code interpreter files', () => {
    expect(guideHasSandboxGatingPayload({
      existingFiles: [{ id: '1', folderKind: 'CodeInterpreter', relativePath: 'run.py', created: '' }],
      newFiles: [],
      skills: [],
      pendingSkillUploads: [],
    })).toBe(true);
  });
});
