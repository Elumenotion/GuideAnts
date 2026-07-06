import type {
  AssistantSkillDto,
  AssistantSkillSaveDto,
  FileDto,
  FileUploadDto,
} from '../../../types/guides';

export function isMaterializableSkillPayloadPath(relativePath: string): boolean {
  const normalized = relativePath.replace(/\\/g, '/');
  if (!normalized.startsWith('Skills/')) {
    return false;
  }

  const parts = normalized.split('/');
  if (parts.length < 4) {
    return false;
  }

  const payloadFolder = parts[2];
  return payloadFolder === 'scripts' || payloadFolder === 'assets';
}

export function guideHasSkillScriptsPayload(params: {
  existingFiles: FileDto[];
  newFiles: FileUploadDto[];
  skills: AssistantSkillDto[];
  pendingSkillUploads: AssistantSkillSaveDto[];
}): boolean {
  const { existingFiles, newFiles, skills, pendingSkillUploads } = params;

  if (existingFiles.some(
    (file) => file.folderKind === 'Skill' && isMaterializableSkillPayloadPath(file.relativePath),
  )) {
    return true;
  }

  if (newFiles.some(
    (file) => file.folderKind === 'Skill' && isMaterializableSkillPayloadPath(file.relativePath),
  )) {
    return true;
  }

  for (const skill of skills) {
    if (skill.files.some((file) => isMaterializableSkillPayloadPath(file.relativePath))) {
      return true;
    }
  }

  for (const upload of pendingSkillUploads) {
    if (upload.filesToAdd?.some((file) => isMaterializableSkillPayloadPath(file.relativePath))) {
      return true;
    }
  }

  return false;
}

export function guideHasSandboxGatingPayload(params: {
  existingFiles: FileDto[];
  newFiles: FileUploadDto[];
  skills: AssistantSkillDto[];
  pendingSkillUploads: AssistantSkillSaveDto[];
}): boolean {
  if (params.existingFiles.some((file) => file.folderKind === 'CodeInterpreter')) {
    return true;
  }

  if (params.newFiles.some((file) => file.folderKind === 'CodeInterpreter')) {
    return true;
  }

  return guideHasSkillScriptsPayload(params);
}

export const FILES_CONTEXT_OPTION_KEY = 'files';
export const FILES_CONTEXT_OPTION_VALUE = '[@files]';

export function hasFilesContextOption(
  contextOptions: Array<{ value?: string }>,
): boolean {
  return contextOptions.some((option) =>
    option.value?.toLowerCase().includes('[@files]') ?? false,
  );
}

export function isNotebookPayloadUpload(file: {
  folderKind: string;
  relativePath: string;
}): boolean {
  if (file.folderKind === 'CodeInterpreter') {
    return true;
  }

  return file.folderKind === 'Skill' && isMaterializableSkillPayloadPath(file.relativePath);
}

export function withSuggestedFilesContextOption<T extends { key: string; value?: string }>(
  contextOptions: T[],
  payloadFiles: Array<{ folderKind: string; relativePath: string }>,
): T[] {
  if (hasFilesContextOption(contextOptions)) {
    return contextOptions;
  }

  if (!payloadFiles.some(isNotebookPayloadUpload)) {
    return contextOptions;
  }

  return [
    ...contextOptions,
    { key: FILES_CONTEXT_OPTION_KEY, value: FILES_CONTEXT_OPTION_VALUE } as T,
  ];
}
