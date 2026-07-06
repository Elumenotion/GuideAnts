import { api } from '../../../../services/api';
import type {
  AssistantSkillDto,
  AssistantSkillSaveDto,
  CreateAssistantDto,
  FileUploadDto,
} from '../../../../types/guides';
import { decodePendingFileContent } from './skillFileTreeModel';
import { mapSkillPrerequisites } from './skillToolsetMapping';
import { withSuggestedFilesContextOption } from '../executablePayload';

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

export function isSkillManifestPath(relativePath: string): boolean {
  return relativePath.endsWith('/SKILL.md') || relativePath.endsWith('SKILL.md');
}

/** Payload files copied onto a new assistant; SKILL.md content lives in instructions only. */
export function filterSkillPayloadFilesForAssistant(files: FileUploadDto[]): FileUploadDto[] {
  return files.filter((file) => !isSkillManifestPath(file.relativePath));
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

export function buildAssistantInstructionsFromSkillMarkdown(markdown: string): string {
  return markdown.trim();
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
    throw new Error(`Skill '${skill.name}' is not saved yet. Save the guide first.`);
  }

  if (!assistantId) {
    throw new Error('Save the guide before creating an assistant from saved skills.');
  }

  const blob = await api.guides.assistants.downloadFile(assistantId, manifest.id);
  return blob.text();
}

async function buildSkillPayloadFiles(
  skill: AssistantSkillDto,
  pendingSkillUploads: AssistantSkillSaveDto[],
  assistantId?: string,
): Promise<FileUploadDto[]> {
  const pending = pendingSkillUploads.find((item) => item.name === skill.name);
  if (pending?.filesToAdd && pending.filesToAdd.length > 0) {
    return filterSkillPayloadFilesForAssistant(pending.filesToAdd);
  }

  const persistedPayloadFiles = skill.files.filter(
    (file) => !file.id.startsWith('pending-') && !isSkillManifestPath(file.relativePath),
  );
  if (persistedPayloadFiles.length === 0) {
    return [];
  }

  if (!assistantId) {
    throw new Error('Save the guide before creating an assistant from saved skills.');
  }

  const uploads: FileUploadDto[] = [];
  for (const file of persistedPayloadFiles) {
    const blob = await api.guides.assistants.downloadFile(assistantId, file.id);
    uploads.push({
      folderKind: 'Skill',
      relativePath: file.relativePath,
      contentBytes: await blobToBase64(blob),
      contentType: file.contentType,
    });
  }

  return uploads;
}

export async function buildCreateAssistantFromSkillPayload(
  projectId: string,
  skills: AssistantSkillDto[],
  pendingSkillUploads: AssistantSkillSaveDto[],
  assistantId: string | undefined,
  selection: CreateFromSkillSelection,
): Promise<CreateAssistantDto> {
  const primary = skills.find((skill) => skill.name === selection.primarySkillName);
  if (!primary) {
    throw new Error('Primary skill not found.');
  }

  const selectedSkills = skills.filter((skill) => selection.selectedSkillNames.includes(skill.name));
  const mapping = mapSkillPrerequisites(
    selectedSkills.flatMap((skill) => skill.requiresToolsets),
    selectedSkills.flatMap((skill) => skill.requiresTools),
  );
  const primaryMarkdown = await resolveSkillMarkdown(primary, pendingSkillUploads, assistantId);
  const instructions = buildAssistantInstructionsFromSkillMarkdown(primaryMarkdown);

  const files: FileUploadDto[] = [];
  for (const skill of selectedSkills) {
    files.push(...await buildSkillPayloadFiles(skill, pendingSkillUploads, assistantId));
  }
  if (mapping.needsCodeInterpreter) {
    files.push({
      folderKind: 'CodeInterpreter',
      relativePath: `skills-${primary.name}-sandbox-placeholder.txt`,
      contentBytes: btoa(`# Sandbox placeholder for skill '${primary.name}'\n`),
      contentType: 'text/plain',
    });
  }

  return {
    projectId,
    name: primary.name,
    description: primary.description,
    instructions,
    toolIds: mapping.toolIds,
    files: files.length > 0 ? files : undefined,
    contextOptions: files.length > 0
      ? withSuggestedFilesContextOption([], files)
      : undefined,
  };
}
