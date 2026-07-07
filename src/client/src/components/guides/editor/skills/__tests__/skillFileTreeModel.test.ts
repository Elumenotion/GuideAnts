import { describe, expect, it } from 'vitest';
import type { FileDto } from '../../../../../types/guides';
import {
  buildSkillFileTree,
  decodePendingFileContent,
  isSkillFilePreviewable,
  skillPackagePath,
} from '../skillFileTreeModel';

function makeFile(relativePath: string, id = 'file-1'): FileDto {
  return {
    id,
    folderKind: 'Skill',
    relativePath,
    created: '2026-01-01T00:00:00Z',
  };
}

describe('skillFileTreeModel', () => {
  it('extracts package-relative paths for a skill', () => {
    expect(skillPackagePath('Skills/demo/scripts/run.py', 'demo')).toBe('scripts/run.py');
    expect(skillPackagePath('Skills/other/scripts/run.py', 'demo')).toBe('scripts/run.py');
    expect(skillPackagePath('readme.md', 'demo')).toBe('readme.md');
  });

  it('builds a nested tree with folders before files and SKILL.md first', () => {
    const tree = buildSkillFileTree(
      [
        makeFile('Skills/demo/references/guide.md', 'ref'),
        makeFile('Skills/demo/scripts/run.py', 'script'),
        makeFile('Skills/demo/SKILL.md', 'manifest'),
      ],
      'demo',
    );

    expect(tree.map((node) => node.name)).toEqual(['references', 'scripts', 'SKILL.md']);
    expect(tree[2].isFolder).toBe(false);
    expect(tree[0].children[0].name).toBe('guide.md');
    expect(tree[1].children[0].name).toBe('run.py');
  });

  it('detects previewable skill file extensions', () => {
    expect(isSkillFilePreviewable('Skills/demo/scripts/run.py')).toBe(true);
    expect(isSkillFilePreviewable('Skills/demo/assets/logo.png')).toBe(false);
  });

  it('decodes pending file content from base64', () => {
    const encoded = btoa(unescape(encodeURIComponent('# Skill\n\nBody')));
    expect(decodePendingFileContent(encoded)).toBe('# Skill\n\nBody');
  });
});
