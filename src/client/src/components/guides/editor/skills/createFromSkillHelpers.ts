import { API_BASE_URL } from '../../../../config/apiConfig';
import type {
  AssistantSkillDto,
  AssistantSkillSaveDto,
  FileUploadDto,
} from '../../../../types/guides';
import { parseSkillFrontmatter } from './skillFrontmatter';
import { decodePendingFileContent } from './skillFileTreeModel';

export interface CreateFromSkillSelection {
  primarySkillName: string;
  selectedSkillNames: string[];
}

async function blobToBase64(blob: Blob): Promise<string> {
  const arrayBuffer = await blob.arrayBuffer();
  const uint8Array = new Uint8Array(arrayBuffer);
  let binaryString = '';
  const chunkSize = 8192;
  for (let i = 0; i < uint8Array.length; i += chunkSize) {
    const chunk = uint8Array.subarray(i, i + chunkSize);
    binaryString += String.fromCharCode(...chunk);
  }

  return btoa(binaryString);
}

function isSkillManifestPath(relativePath: string): boolean {
  return relativePath.endsWith('/SKILL.md') || relativePath.endsWith('SKILL.md');
}

function findPendingSkillMarkdown(
  skillName: string,
  pendingSkillUploads: AssistantSkillSaveDto[],
): string | undefined {
  const pending = pendingSkillUploads.find((item) => item.name === skillName);
  const manifest = pending?.filesToAdd?.find((file) => isSkillManifestPath(file.relativePath));
  if (!manifest?.contentBytes) {
    return undefined;
  }

  return decodePendingFileContent(manifest.contentBytes);
}

async function downloadAssistantFile(
  assistantId: string,
  fileId: string,
): Promise<Blob> {
  const response = await fetch(
    `${API_BASE_URL}/assistants/${assistantId}/files/${fileId}/download`,
  );
  if (!response.ok) {
    throw new Error('Failed to download skill file content.');
  }

  return response.blob();
}

export function buildAssistantInstructionsFromSkillMarkdown(markdown: string): string {
  const parsed = parseSkillFrontmatter(markdown);
  const body = parsed.body.trim();
  if (body.length > 0) {
    return body;
  }

  return parsed.originalMarkdown.trim();
}

export async function resolveSkillMarkdown(
  skill: AssistantSkillDto,
  pendingSkillUploads: AssistantSkillSaveDto[],
  assistantId?: string,
): Promise<string> {
  const pendingMarkdown = findPendingSkillMarkdown(skill.name, pendingSkillUploads);
  if (pendingMarkdown) {
    return pendingMarkdown;
  }

  const manifest = skill.files.find((file) => isSkillManifestPath(file.relativePath));
  if (!manifest || manifest.id.startsWith('pending-')) {
    throw new Error(`Save ${skill.name} before creating an assistant from it.`);
  }

  if (!assistantId) {
    throw new Error(`Save the guide before creating an assistant from ${skill.name}.`);
  }

  const blob = await downloadAssistantFile(assistantId, manifest.id);
  return blob.text();
}

async function buildSkillFilesToAdd(
  skill: AssistantSkillDto,
  pendingSkillUploads: AssistantSkillSaveDto[],
  assistantId?: string,
): Promise<FileUploadDto[]> {
  const pending = pendingSkillUploads.find((item) => item.name === skill.name);
  if (pending?.filesToAdd && pending.filesToAdd.length > 0) {
    return pending.filesToAdd;
  }

  const persistedFiles = skill.files.filter((file) => !file.id.startsWith('pending-'));
  if (persistedFiles.length === 0) {
    throw new Error(`Skill '${skill.name}' has no files to copy. Save it first.`);
  }

  if (!assistantId) {
    throw new Error(`Save the guide before creating an assistant from ${skill.name}.`);
  }

  const uploads: FileUploadDto[] = [];
  for (const file of persistedFiles) {
    const blob = await downloadAssistantFile(assistantId, file.id);
    uploads.push({
      folderKind: 'Skill',
      relativePath: file.relativePath,
      contentBytes: await blobToBase64(blob),
      contentType: file.contentType,
    });
  }

  return uploads;
}

export async function buildCreateFromSkillUploads(
  skills: AssistantSkillDto[],
  pendingSkillUploads: AssistantSkillSaveDto[],
  assistantId: string | undefined,
  selection: CreateFromSkillSelection,
): Promise<AssistantSkillSaveDto[]> {
  const selectedSkills = skills.filter((skill) => selection.selectedSkillNames.includes(skill.name));
  const uploads: AssistantSkillSaveDto[] = [];

  for (const skill of selectedSkills) {
    const filesToAdd = await buildSkillFilesToAdd(skill, pendingSkillUploads, assistantId);
    const pending = pendingSkillUploads.find((item) => item.name === skill.name);

    uploads.push({
      name: skill.name,
      description: skill.description,
      enabled: skill.enabled,
      displayOrder: skill.displayOrder,
      source: pending?.source ?? skill.source,
      filesToAdd,
    });
  }

  return uploads;
}
