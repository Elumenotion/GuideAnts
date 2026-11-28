import { describe, expect, it } from 'vitest';
import {
  catalogBadgeClassName,
  catalogBadgeLabel,
  catalogBadgeState,
  catalogPackageToImportResult,
  canUpdateFromCatalog,
  decodeBase64ToText,
  isLocalSkillConflict,
  skillPackagePath,
} from '../skillStoreHelpers';
import type { AssistantSkillDto, SkillCatalogEntryDto, SkillCatalogPackageDto } from '../../../../../types/guides';

const MARKDOWN = ['---', 'name: html-craft', 'description: Build and verify HTML pages.', '---', '# Body'].join('\n');

function toBase64(value: string): string {
  return btoa(unescape(encodeURIComponent(value)));
}

function pkg(overrides: Partial<SkillCatalogPackageDto> = {}): SkillCatalogPackageDto {
  return {
    name: 'html-craft',
    version: 3,
    files: [
      { relativePath: 'SKILL.md', contentType: 'text/markdown', contentBase64: toBase64(MARKDOWN) },
      { relativePath: 'scripts/build.sh', contentType: null, contentBase64: toBase64('echo build') },
    ],
    ...overrides,
  };
}

function skill(overrides: Partial<AssistantSkillDto> = {}): AssistantSkillDto {
  return {
    name: 'html-craft',
    description: 'Build and verify HTML pages.',
    enabled: true,
    displayOrder: 0,
    source: 'Imported',
    files: [],
    requiresToolsets: [],
    requiresTools: [],
    fallbackForToolsets: [],
    fallbackForTools: [],
    ...overrides,
  };
}

function entry(overrides: Partial<SkillCatalogEntryDto> = {}): SkillCatalogEntryDto {
  return {
    id: 'catalog-1',
    name: 'html-craft',
    description: 'Build and verify HTML pages.',
    currentVersion: 3,
    fileCount: 2,
    updated: '2026-11-27T00:00:00Z',
    ...overrides,
  };
}

describe('skillPackagePath', () => {
  it('prefixes the assistant skill folder', () => {
    expect(skillPackagePath('html-craft', 'SKILL.md')).toBe('Skills/html-craft/SKILL.md');
    expect(skillPackagePath('html-craft', '/references/setup.md'))
      .toBe('Skills/html-craft/references/setup.md');
  });
});

describe('catalogPackageToImportResult', () => {
  it('stages every snapshot file under the assistant skill folder', () => {
    const result = catalogPackageToImportResult(pkg(), 4);
    expect(result.skill.filesToAdd?.map((file) => file.relativePath)).toEqual([
      'Skills/html-craft/SKILL.md',
      'Skills/html-craft/scripts/build.sh',
    ]);
    expect(result.skill.filesToAdd?.every((file) => file.folderKind === 'Skill')).toBe(true);
    expect(result.skill.displayOrder).toBe(4);
  });

  it('recovers the description and markdown from the snapshot manifest', () => {
    const result = catalogPackageToImportResult(pkg(), 0);
    expect(result.skill.description).toBe('Build and verify HTML pages.');
    expect(result.originalMarkdown).toBe(MARKDOWN);
    expect(result.skill.name).toBe('html-craft');
  });

  it('still installs when the manifest cannot be parsed', () => {
    const broken = pkg({
      files: [{ relativePath: 'SKILL.md', contentType: null, contentBase64: toBase64('no frontmatter') }],
    });
    const result = catalogPackageToImportResult(broken, 1);
    expect(result.skill.description).toBe('');
    expect(result.skill.filesToAdd).toHaveLength(1);
  });

  it('decodeBase64ToText round-trips non-ascii text', () => {
    expect(decodeBase64ToText(toBase64('café – skill'))).toBe('café – skill');
  });
});

describe('catalogBadgeState', () => {
  it('reports local when there is no pin', () => {
    expect(catalogBadgeState(skill())).toBe('local');
    expect(catalogBadgeState(skill({ catalogSkillId: 'x', catalogVersion: null }))).toBe('local');
  });

  it('reports installed when pinned to the newest version', () => {
    const pinned = skill({ catalogSkillId: 'catalog-1', catalogVersion: 3, catalogCurrentVersion: 3 });
    expect(catalogBadgeState(pinned)).toBe('installed');
    expect(catalogBadgeLabel('installed', pinned)).toBe('Store v3');
    expect(canUpdateFromCatalog(pinned)).toBe(false);
  });

  it('offers an update only when the catalog is newer', () => {
    const stale = skill({ catalogSkillId: 'catalog-1', catalogVersion: 1, catalogCurrentVersion: 3 });
    expect(catalogBadgeState(stale)).toBe('update-available');
    expect(canUpdateFromCatalog(stale)).toBe(true);
  });

  it('reports modified when the entry has gone from the catalog', () => {
    const orphan = skill({ catalogSkillId: 'catalog-1', catalogVersion: 1, catalogCurrentVersion: null });
    expect(catalogBadgeState(orphan)).toBe('modified');
    expect(catalogBadgeLabel('modified', orphan)).toBe('Store (modified locally)');
  });

  it('gives every state its own badge class', () => {
    const classes = (['local', 'installed', 'update-available', 'modified'] as const)
      .map((state) => catalogBadgeClassName(state));
    expect(new Set(classes).size).toBe(4);
  });
});

describe('isLocalSkillConflict', () => {
  it('is false when the skill is not present', () => {
    expect(isLocalSkillConflict(entry(), [])).toBe(false);
  });

  it('is true for a same-named local skill', () => {
    expect(isLocalSkillConflict(entry(), [skill()])).toBe(true);
  });

  it('is false for a pinned install of the same catalog entry', () => {
    const pinned = skill({ catalogSkillId: 'catalog-1', catalogVersion: 1, catalogCurrentVersion: 3 });
    expect(isLocalSkillConflict(entry(), [pinned])).toBe(false);
  });

  it('is true when the same name points at a different catalog entry', () => {
    const pinned = skill({ catalogSkillId: 'other', catalogVersion: 1, catalogCurrentVersion: 1 });
    expect(isLocalSkillConflict(entry(), [pinned])).toBe(true);
  });
});
