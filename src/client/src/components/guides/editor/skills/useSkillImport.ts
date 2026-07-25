import { useCallback, useState } from 'react';
import {
  buildSkillUploadsFromFolder,
  buildSkillUploadsFromZip,
  type SkillImportOptions,
} from './skillImportHelpers';
import {
  getSkillFrontmatterErrorDetails,
  type SkillFrontmatterErrorDetails,
} from './skillFrontmatterErrors';

interface SkillImportRepairContext {
  files: File[];
  repairedMarkdown: string;
  source: 'folder' | 'zip';
  zipFile?: File;
}

export function useSkillImport() {
  const [error, setError] = useState<SkillFrontmatterErrorDetails | null>(null);
  const [isImporting, setIsImporting] = useState(false);
  const [repairContext, setRepairContext] = useState<SkillImportRepairContext | null>(null);

  const captureImportError = useCallback((importError: unknown) => {
    const details = getSkillFrontmatterErrorDetails(importError);
    if (details) {
      setError(details);
      return;
    }

    const message = importError instanceof Error ? importError.message : 'Failed to import skill package.';
    setError({
      title: 'Could not import skill',
      problem: message,
      fix: 'Verify the package contains exactly one SKILL.md with valid YAML frontmatter.',
      snippetLines: [],
      canRepair: false,
    });
  }, []);

  const importFolder = useCallback(async (files: FileList | File[], options?: SkillImportOptions) => {
    setIsImporting(true);
    setError(null);
    setRepairContext(null);
    try {
      return await buildSkillUploadsFromFolder(files, options);
    } catch (importError) {
      captureImportError(importError);
      const details = getSkillFrontmatterErrorDetails(importError);
      if (details?.canRepair && details.repairedMarkdown) {
        setRepairContext({
          files: Array.from(files),
          repairedMarkdown: details.repairedMarkdown,
          source: 'folder',
        });
      }
      throw importError;
    } finally {
      setIsImporting(false);
    }
  }, [captureImportError]);

  const importZip = useCallback(async (file: File, options?: SkillImportOptions) => {
    setIsImporting(true);
    setError(null);
    setRepairContext(null);
    try {
      return await buildSkillUploadsFromZip(file, options);
    } catch (importError) {
      captureImportError(importError);
      const details = getSkillFrontmatterErrorDetails(importError);
      if (details?.canRepair && details.repairedMarkdown) {
        setRepairContext({
          files: [],
          repairedMarkdown: details.repairedMarkdown,
          source: 'zip',
          zipFile: file,
        });
      }
      throw importError;
    } finally {
      setIsImporting(false);
    }
  }, [captureImportError]);

  const importRepaired = useCallback(async () => {
    if (!repairContext) {
      return null;
    }

    setIsImporting(true);
    setError(null);
    try {
      if (repairContext.source === 'zip' && repairContext.zipFile) {
        return await buildSkillUploadsFromZip(repairContext.zipFile, {
          skillMarkdownOverride: repairContext.repairedMarkdown,
        });
      }

      return await buildSkillUploadsFromFolder(repairContext.files, {
        skillMarkdownOverride: repairContext.repairedMarkdown,
      });
    } catch (importError) {
      captureImportError(importError);
      throw importError;
    } finally {
      setIsImporting(false);
    }
  }, [captureImportError, repairContext]);

  const clearError = useCallback(() => {
    setError(null);
    setRepairContext(null);
  }, []);

  return {
    error,
    isImporting,
    repairContext,
    importFolder,
    importZip,
    importRepaired,
    clearError,
  };
}

export type { SkillImportResult as ImportedSkillPackage } from './skillImportHelpers';
