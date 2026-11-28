import type {
  AssistantSkillDto,
  FileUploadDto,
  SkillCatalogEntryDto,
  SkillCatalogPackageDto,
} from '../../../../types/guides';
import { normalizeSkillDescription } from './skillDescriptionLimits';
import { parseSkillFrontmatter } from './skillFrontmatter';
import type { SkillImportResult } from './skillImportHelpers';

/** Server-side folder kind for skill payload files. */
const SKILL_FOLDER_KIND = 'Skill';

export function skillPackagePath(skillName: string, relativePath: string): string {
  const trimmed = relativePath.replace(/^\/+/, '');
  return `Skills/${skillName}/${trimmed}`;
}

/**
 * Stages a catalog snapshot as a pending skill upload, so an install from the store follows the
 * exact same save path as a folder or .zip import: files land as AssistantFile rows on save and
 * the metadata sidecar is rebuilt server-side.
 */
export function decodeBase64ToText(contentBase64: string): string {
  const binary = atob(contentBase64);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

export function catalogPackageToImportResult(
  pkg: SkillCatalogPackageDto,
  displayOrder: number,
): SkillImportResult {
  const filesToAdd: FileUploadDto[] = pkg.files.map((file) => ({
    folderKind: SKILL_FOLDER_KIND,
    relativePath: skillPackagePath(pkg.name, file.relativePath),
    contentBytes: file.contentBase64,
    contentType: file.contentType ?? undefined,
  }));

  const manifest = pkg.files.find((file) => file.relativePath === 'SKILL.md');
  const originalMarkdown = manifest ? decodeBase64ToText(manifest.contentBase64) : '';

  let description = '';
  try {
    description = normalizeSkillDescription(
      parseSkillFrontmatter(originalMarkdown).frontmatter.description,
    ).description;
  } catch {
    // A malformed manifest still installs verbatim; the server rejects it on save with a
    // precise message, which beats guessing a description here.
  }

  return {
    skill: {
      name: pkg.name,
      description,
      enabled: true,
      displayOrder,
      source: 'Imported',
      filesToAdd,
    },
    originalMarkdown,
    descriptionWarnings: [],
  };
}

export type CatalogBadgeState = 'local' | 'installed' | 'update-available' | 'modified';

/**
 * Badge for a skill card. The server clears a pin as soon as local bytes drift from the pinned
 * snapshot, so 'modified' only appears transiently between an edit and the next save.
 */
export function catalogBadgeState(skill: {
  catalogSkillId?: string | null;
  catalogVersion?: number | null;
  catalogCurrentVersion?: number | null;
}): CatalogBadgeState {
  if (!skill.catalogSkillId || skill.catalogVersion == null) {
    return 'local';
  }
  const current = skill.catalogCurrentVersion;
  if (current == null) {
    return 'modified';
  }
  return current > skill.catalogVersion ? 'update-available' : 'installed';
}

export function catalogBadgeLabel(state: CatalogBadgeState, skill: {
  catalogVersion?: number | null;
}): string {
  switch (state) {
    case 'installed':
      return `Store v${skill.catalogVersion}`;
    case 'update-available':
      return `Store v${skill.catalogVersion} (update available)`;
    case 'modified':
      return 'Store (modified locally)';
    default:
      return 'Local';
  }
}

export function catalogBadgeClassName(state: CatalogBadgeState): string {
  switch (state) {
    case 'installed':
      return 'bg-emerald-100 text-emerald-800';
    case 'update-available':
      return 'bg-amber-100 text-amber-900';
    case 'modified':
      return 'bg-violet-100 text-violet-800';
    default:
      return 'bg-gray-100 text-gray-700';
  }
}

export function canUpdateFromCatalog(skill: AssistantSkillDto): boolean {
  return catalogBadgeState(skill) === 'update-available';
}

export function isLocalSkillConflict(
  entry: SkillCatalogEntryDto,
  skills: AssistantSkillDto[],
): boolean {
  const existing = skills.find((skill) => skill.name === entry.name);
  if (!existing) {
    return false;
  }
  // A pinned install of the same entry is a reinstall, not a conflict.
  return existing.catalogSkillId !== entry.id;
}
