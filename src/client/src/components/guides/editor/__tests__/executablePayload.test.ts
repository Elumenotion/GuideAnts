import { describe, it, expect } from 'vitest';
import {
  guideHasSandboxGatingPayload,
  guideHasSkillScriptsPayload,
  isMaterializableSkillPayloadPath,
  withSuggestedFilesContextOption,
  FILES_CONTEXT_OPTION_KEY,
  FILES_CONTEXT_OPTION_VALUE,
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
        fallbackForToolsets: [],
        fallbackForTools: [],
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

describe('withSuggestedFilesContextOption', () => {
  it('adds files context option when notebook payload files are present', () => {
    const result = withSuggestedFilesContextOption([], [{
      folderKind: 'Skill',
      relativePath: 'Skills/searxng-search/scripts/searxng.sh',
    }]);

    expect(result).toEqual([{
      key: FILES_CONTEXT_OPTION_KEY,
      value: FILES_CONTEXT_OPTION_VALUE,
    }]);
  });

  it('does not duplicate when [@files] is already configured', () => {
    const existing = [{ key: 'workspace', value: '[@files]' }];
    const result = withSuggestedFilesContextOption(existing, [{
      folderKind: 'Skill',
      relativePath: 'Skills/demo/scripts/run.py',
    }]);

    expect(result).toBe(existing);
  });
});
