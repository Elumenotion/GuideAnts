import JSZip from 'jszip';
import type { AssistantSkillDto, AssistantSkillSaveDto, FileUploadDto } from '../../../../types/guides';
import { parseSkillFrontmatter, buildCanonicalSkillMarkdown } from './skillFrontmatter';
import {
  buildSkillDescriptionImportWarnings,
  normalizeSkillDescription,
} from './skillDescriptionLimits';

async function fileToBase64(file: File): Promise<string> {
  const arrayBuffer = await file.arrayBuffer();
  const uint8Array = new Uint8Array(arrayBuffer);
  let binaryString = '';
  const chunkSize = 8192;
  for (let i = 0; i < uint8Array.length; i += chunkSize) {
    const chunk = uint8Array.subarray(i, i + chunkSize);
    binaryString += String.fromCharCode(...chunk);
  }

  return btoa(binaryString);
}

function normalizePath(path: string): string {
  return path.replace(/\\/g, '/').replace(/^\/+/, '');
}

function stripRoot(path: string, root: string): string {
  if (!root) {
    return path;
  }

  if (path === root) {
    return 'SKILL.md';
  }

  return path.slice(root.length + 1);
}

function resolveSkillRoot(paths: string[]): string {
  const manifests = paths.filter((path) => path.endsWith('/SKILL.md') || path === 'SKILL.md');
  if (manifests.length !== 1) {
    throw new Error('Skill package must contain exactly one SKILL.md file.');
  }

  const manifestPath = manifests[0];
  const slashIndex = manifestPath.lastIndexOf('/');
  return slashIndex < 0 ? '' : manifestPath.slice(0, slashIndex);
}

export interface SkillImportResult {
  skill: AssistantSkillSaveDto;
  originalMarkdown: string;
  descriptionWarnings: string[];
}

export async function buildSkillUploadsFromFolder(files: FileList | File[]): Promise<SkillImportResult> {
  const entries = new Map<string, File>();
  for (const file of Array.from(files)) {
    const path = normalizePath((file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name);
    if (path.split('/').includes('..')) {
      throw new Error(`Path traversal is not allowed: ${path}`);
    }

    entries.set(path, file);
  }

  const paths = [...entries.keys()];
  const root = resolveSkillRoot(paths);
  const manifestPath = root ? `${root}/SKILL.md` : 'SKILL.md';
  const manifestFile = entries.get(manifestPath);
  if (!manifestFile) {
    throw new Error('Skill package must contain a SKILL.md file.');
  }

  const originalMarkdown = await manifestFile.text();
  const parsed = parseSkillFrontmatter(originalMarkdown);
  const normalizedDescription = normalizeSkillDescription(parsed.frontmatter.description);
  const canonicalMarkdown = normalizedDescription.truncated
    ? buildCanonicalSkillMarkdown({
        name: parsed.frontmatter.name,
        description: normalizedDescription.description,
        enabled: parsed.frontmatter.enabled,
        displayOrder: parsed.frontmatter.displayOrder,
        body: parsed.body,
        source: parsed.frontmatter.source ?? 'Imported',
        requiresToolsets: parsed.frontmatter.requiresToolsets,
        requiresTools: parsed.frontmatter.requiresTools,
      })
    : originalMarkdown;
  const skillFolder = `Skills/${parsed.frontmatter.name}`;
  const uploads: FileUploadDto[] = [];

  for (const [path, file] of entries) {
    if (root && path !== root && !path.startsWith(`${root}/`)) {
      continue;
    }

    const packageRelative = stripRoot(path, root);
    const relativePath = `${skillFolder}/${packageRelative}`;
    const contentBytes = relativePath.endsWith('/SKILL.md') || relativePath.endsWith('SKILL.md')
      ? btoa(unescape(encodeURIComponent(canonicalMarkdown)))
      : await fileToBase64(file);

    uploads.push({
      folderKind: 'Skill',
      relativePath,
      contentBytes,
      contentType: file.type || undefined,
    });
  }

  return {
    originalMarkdown: canonicalMarkdown,
    descriptionWarnings: buildSkillDescriptionImportWarnings(normalizedDescription),
    skill: {
      name: parsed.frontmatter.name,
      description: normalizedDescription.description,
      enabled: parsed.frontmatter.enabled,
      displayOrder: parsed.frontmatter.displayOrder,
      source: 'Imported',
      filesToAdd: uploads,
    },
  };
}

export async function buildSkillUploadsFromZip(file: File): Promise<SkillImportResult> {
  const zip = await JSZip.loadAsync(file);
  const files: File[] = [];

  await Promise.all(
    Object.entries(zip.files).map(async ([path, entry]) => {
      if (entry.dir) {
        return;
      }

      const content = await entry.async('blob');
      const zipFile = new File([content], path.split('/').pop() ?? path, {
        type: content.type || undefined,
      });
      Object.defineProperty(zipFile, 'webkitRelativePath', { value: path });
      files.push(zipFile);
    }),
  );

  return buildSkillUploadsFromFolder(files);
}

export function toSkillSaveDto(skill: AssistantSkillDto): AssistantSkillSaveDto {
  return {
    name: skill.name,
    description: skill.description,
    enabled: skill.enabled,
    displayOrder: skill.displayOrder,
    source: skill.source,
    fileIdsToKeep: skill.files.map((file) => file.id),
  };
}

export function buildAuthoredSkillSave(input: {
  name: string;
  description: string;
  body: string;
  enabled: boolean;
  displayOrder: number;
  referenceFiles?: File[];
}): Promise<AssistantSkillSaveDto> {
  const normalizedDescription = normalizeSkillDescription(input.description);
  const markdown = buildCanonicalSkillMarkdown({
    name: input.name,
    description: normalizedDescription.description,
    enabled: input.enabled,
    displayOrder: input.displayOrder,
    body: input.body,
    source: 'Authored',
  });

  const uploads: FileUploadDto[] = [{
    folderKind: 'Skill',
    relativePath: `Skills/${input.name}/SKILL.md`,
    contentBytes: btoa(unescape(encodeURIComponent(markdown))),
    contentType: 'text/markdown',
  }];

  const addReferences = (input.referenceFiles ?? []).map(async (file) => {
    uploads.push({
      folderKind: 'Skill',
      relativePath: `Skills/${input.name}/references/${file.name}`,
      contentBytes: await fileToBase64(file),
      contentType: file.type || undefined,
    });
  });

  return Promise.all(addReferences).then(() => ({
    name: input.name,
    description: normalizedDescription.description,
    enabled: input.enabled,
    displayOrder: input.displayOrder,
    source: 'Authored' as const,
    filesToAdd: uploads,
  }));
}
